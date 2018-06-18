﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DevExpress.Xpo;
using DevExpress.Xpo.Helpers;
using System.Collections.Concurrent;

namespace ODataService.Helpers {

    static class IQueryableExtensions {
        public static XpoLinqQuery<T> AsWrappedQuery<T>(this XPQuery<T> source) {
            return new XpoLinqQuery<T>(source);
        }
    }

    public class XpoLinqQuery<T> : IOrderedQueryable<T> {

        readonly Expression queryExpression;
        readonly IQueryProvider queryProvider;

        public XpoLinqQuery(XPQuery<T> query) : this(new XpoLinqQueryProvider(query, query.Session), ((IQueryable)query).Expression) {
        }

        public XpoLinqQuery(XpoLinqQueryProvider queryProvider) {
            if(queryProvider == null) {
                throw new ArgumentNullException("provider");
            }
            this.queryProvider = queryProvider;
            this.queryExpression = Expression.Constant(this);
        }

        public XpoLinqQuery(IQueryProvider queryProvider, Expression queryExpression) {
            if(queryProvider == null) {
                throw new ArgumentNullException("provider");
            }
            if(queryExpression == null) {
                throw new ArgumentNullException("expression");
            }
            if(!queryExpression.Type.IsGenericType) {
                throw new ArgumentOutOfRangeException("expression");
            }
            this.queryProvider = queryProvider;
            this.queryExpression = queryExpression;
        }

        #region IEnumerable<T> Members

        public IEnumerator<T> GetEnumerator() {
            object result = this.queryProvider.Execute(this.queryExpression);
            return ((IEnumerable<T>)result).GetEnumerator();
        }

        #endregion

        #region IEnumerable Members

        IEnumerator IEnumerable.GetEnumerator() {
            return ((IEnumerable)this.queryProvider.Execute(this.queryExpression)).GetEnumerator();
        }        

        #endregion

        #region IQueryable Members

        public Type ElementType {
            get { return typeof(T); }
        }

        public Expression Expression {
            get { return this.queryExpression; }
        }

        public IQueryProvider Provider {
            get { return this.queryProvider; }
        }
        #endregion
    }

    public class XpoLinqQueryProvider : IQueryProvider {

        readonly Session session;
        readonly IQueryProvider queryProviderInternal;

        public XpoLinqQueryProvider(IQueryProvider baseQueryProvider, Session session) {
            queryProviderInternal = baseQueryProvider;
            this.session = session;
        }

        public IQueryable CreateQuery(Expression expression) {
            try {
                Type elementType = GetExpressionType(expression);
                return CreateQuery(elementType, expression);
            } catch(TargetInvocationException tie) {
                throw tie.InnerException;
            }
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) {
            return new XpoLinqQuery<TElement>(this, expression);
        }

        IQueryable CreateQuery(Type elementType, Expression expression) {
            Type result = typeof(XpoLinqQuery<>).MakeGenericType(elementType);
            ConstructorInfo ci = result.GetConstructor(new Type[] { this.GetType(), typeof(Expression) });
            return (IQueryable)ci.Invoke(new object[] { this, expression });
        }

        public TResult Execute<TResult>(Expression expression) {
            object result = this.Execute(expression);
            return (TResult)result;
        }

        public object Execute(Expression expression) {
            expression = PreprocessExpression(expression);
            if(expression.NodeType == ExpressionType.Constant) {
                ConstantExpression c = (ConstantExpression)expression;
                return (IQueryable)c.Value;
            }
            bool executeSingle = false;
            if(expression.Type.IsGenericType) {
                Type expressionType = TypeSystem.GetElementType(expression.Type);
                if(IsPersistentType(expressionType)) {
                    var queryCreator = GetXPQueryCreator(expressionType);
                    IQueryable xpQuery = ((IQueryProvider)queryCreator.CreateXPQuery(session)).CreateQuery(expression);
                    return xpQuery;
                }
            } else {
                executeSingle = true;
            }
            Type elementType = GetExpressionType(expression);
            if(executeSingle) {
                var queryCreator = GetXPQueryCreator(elementType);
                object result = ((IQueryProvider)queryCreator.CreateXPQuery(session)).Execute(expression);
                return result;
            }

            var xpCreator = GetXPQueryCreator(elementType);
            return xpCreator.Enumerate(((IQueryProvider)xpCreator.CreateXPQuery(session)).CreateQuery(expression));
        }

        Type GetExpressionType(Expression expression) {
            Expression currentExpression = expression;
            Type elementType = null;
            while(elementType == null) {
                if(currentExpression.Type.IsGenericType) {
                    Type expressionType = TypeSystem.GetElementType(currentExpression.Type);
                    if(IsPersistentType(expressionType)) {
                        elementType = expressionType;
                        break;
                    };
                }
                MethodCallExpression call = currentExpression as MethodCallExpression;
                if(call == null)
                    throw new InvalidOperationException();
                currentExpression = call.Arguments[0];
            }
            return elementType;
        }
        

        Expression PreprocessExpression(Expression expression) {
            var preprocessor = new LinqExpressionPreprocessor();
            return preprocessor.Process(expression);
        }

        static readonly ConcurrentDictionary<Type, XPQueryCreatorBase> xpQueryCreatorDict = new ConcurrentDictionary<Type, XPQueryCreatorBase>();

        bool IsPersistentType(Type type) {
            return type.IsSubclassOf(typeof(PersistentBase));
        }

        XPQueryCreatorBase GetXPQueryCreator(Type type) {
            return xpQueryCreatorDict.GetOrAdd(type, t => {
                Type creatorType = typeof(XPQueryCreator<>).MakeGenericType(type);
                XPQueryCreatorBase queryCreator = (XPQueryCreatorBase)Activator.CreateInstance(creatorType);
                return queryCreator;
            });
        }
    }

    abstract class XPQueryCreatorBase {
        public abstract IQueryable CreateXPQuery(Session session);
        public abstract IEnumerable Enumerate(IQueryable queryable);
    }

    class XPQueryCreator<T> : XPQueryCreatorBase {
        public XPQueryCreator() {
        }
        public override IQueryable CreateXPQuery(Session session) {
            return new XPQuery<T>(session);
        }
        public override IEnumerable Enumerate(IQueryable queryable) {
            return queryable.Cast<object>();
        }
    }

    class LinqExpressionPreprocessor : ExpressionVisitor {
        public Expression Process(Expression expression) {
            return Visit(expression);
        }

        protected override Expression VisitBinary(BinaryExpression node) {
            switch(node.NodeType) {
                case ExpressionType.Equal:
                    if(node.Left.NodeType != ExpressionType.Conditional || node.Right.NodeType != ExpressionType.Constant
                    || node.Right.Type != typeof(bool?) || (bool?)(node.Right as ConstantExpression).Value != true) {
                        break;
                    }
                    ConditionalExpression ifNode = (node.Left as ConditionalExpression);
                    if(ifNode.Test.NodeType != ExpressionType.Equal) {
                        break;
                    }
                    BinaryExpression ifTest = (ifNode.Test as BinaryExpression);
                    if(ifTest.Right.NodeType != ExpressionType.Constant || (ifTest.Right as ConstantExpression).Value != null) {
                        break;
                    }
                    if(ifNode.IfTrue.NodeType != ExpressionType.Constant || (ifNode.IfTrue as ConstantExpression).Value != null) {
                        break;
                    }
                    if(ifNode.IfFalse.NodeType == ExpressionType.Convert && ifNode.IfFalse.Type == typeof(bool?)) {
                        return base.Visit((ifNode.IfFalse as UnaryExpression).Operand);
                    }
                    return base.Visit(ifNode.IfFalse);
            }
            return base.VisitBinary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node) {
            switch(node.Method.Name) {
                case "Compare":
                    if(node.Method.DeclaringType == typeof(string) && node.Arguments.Count == 3) {
                        MethodInfo method = typeof(string).GetMethod("Compare", new Type[] { typeof(string), typeof(string) });
                        Expression newExpr = Expression.Call(method, node.Arguments[0], node.Arguments[1]);
                        return base.Visit(newExpr);
                    }
                    break;
            }
            return base.VisitMethodCall(node);
        }
    }
}