// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Extensions.Internal;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Metadata.Internal;
using Microsoft.Data.Entity.Query.ExpressionVisitors;
using Microsoft.Data.Entity.Query.Internal;
using Microsoft.Data.Entity.Query.ResultOperators.Internal;
using Microsoft.Data.Entity.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ExpressionVisitors;
using Remotion.Linq.Clauses.StreamedData;

namespace Microsoft.Data.Entity.Query
{
    public abstract class EntityQueryModelVisitor : QueryModelVisitorBase
    {
        public static readonly ParameterExpression QueryContextParameter
            = Expression.Parameter(typeof(QueryContext), "queryContext");

        public static readonly MethodInfo PropertyMethodInfo
            = typeof(EF).GetTypeInfo().GetDeclaredMethod(nameof(EF.Property));

        protected virtual IModel Model { get; }
        private readonly IQueryOptimizer _queryOptimizer;
        private readonly INavigationRewritingExpressionVisitorFactory _navigationRewritingExpressionVisitorFactory;
        private readonly ISubQueryMemberPushDownExpressionVisitor _subQueryMemberPushDownExpressionVisitor;
        private readonly IQuerySourceTracingExpressionVisitorFactory _querySourceTracingExpressionVisitorFactory;
        private readonly IEntityResultFindingExpressionVisitorFactory _entityResultFindingExpressionVisitorFactory;
        private readonly ITaskBlockingExpressionVisitor _taskBlockingExpressionVisitor;
        private readonly IMemberAccessBindingExpressionVisitorFactory _memberAccessBindingExpressionVisitorFactory;
        private readonly IProjectionExpressionVisitorFactory _projectionExpressionVisitorFactory;
        private readonly IEntityQueryableExpressionVisitorFactory _entityQueryableExpressionVisitorFactory;
        private readonly IQueryAnnotationExtractor _queryAnnotationExtractor;
        private readonly IResultOperatorHandler _resultOperatorHandler;
        private readonly IEntityMaterializerSource _entityMaterializerSource;
        private readonly IExpressionPrinter _expressionPrinter;
        private readonly QueryCompilationContext _queryCompilationContext;

        private Expression _expression;
        private ParameterExpression _currentParameter;

        private int _transparentParameterCounter;

        // TODO: Can these be non-blocking?
        private bool _blockTaskExpressions = true;

        protected EntityQueryModelVisitor(
            [NotNull] IModel model,
            [NotNull] IQueryOptimizer queryOptimizer,
            [NotNull] INavigationRewritingExpressionVisitorFactory navigationRewritingExpressionVisitorFactory,
            [NotNull] ISubQueryMemberPushDownExpressionVisitor subQueryMemberPushDownExpressionVisitor,
            [NotNull] IQuerySourceTracingExpressionVisitorFactory querySourceTracingExpressionVisitorFactory,
            [NotNull] IEntityResultFindingExpressionVisitorFactory entityResultFindingExpressionVisitorFactory,
            [NotNull] ITaskBlockingExpressionVisitor taskBlockingExpressionVisitor,
            [NotNull] IMemberAccessBindingExpressionVisitorFactory memberAccessBindingExpressionVisitorFactory,
            [NotNull] IOrderingExpressionVisitorFactory orderingExpressionVisitorFactory,
            [NotNull] IProjectionExpressionVisitorFactory projectionExpressionVisitorFactory,
            [NotNull] IEntityQueryableExpressionVisitorFactory entityQueryableExpressionVisitorFactory,
            [NotNull] IQueryAnnotationExtractor queryAnnotationExtractor,
            [NotNull] IResultOperatorHandler resultOperatorHandler,
            [NotNull] IEntityMaterializerSource entityMaterializerSource,
            [NotNull] IExpressionPrinter expressionPrinter,
            [NotNull] QueryCompilationContext queryCompilationContext)
        {
            Check.NotNull(model, nameof(model));
            Check.NotNull(queryOptimizer, nameof(queryOptimizer));
            Check.NotNull(navigationRewritingExpressionVisitorFactory, nameof(navigationRewritingExpressionVisitorFactory));
            Check.NotNull(subQueryMemberPushDownExpressionVisitor, nameof(subQueryMemberPushDownExpressionVisitor));
            Check.NotNull(querySourceTracingExpressionVisitorFactory, nameof(querySourceTracingExpressionVisitorFactory));
            Check.NotNull(entityResultFindingExpressionVisitorFactory, nameof(entityResultFindingExpressionVisitorFactory));
            Check.NotNull(taskBlockingExpressionVisitor, nameof(taskBlockingExpressionVisitor));
            Check.NotNull(memberAccessBindingExpressionVisitorFactory, nameof(memberAccessBindingExpressionVisitorFactory));
            Check.NotNull(orderingExpressionVisitorFactory, nameof(orderingExpressionVisitorFactory));
            Check.NotNull(projectionExpressionVisitorFactory, nameof(projectionExpressionVisitorFactory));
            Check.NotNull(entityQueryableExpressionVisitorFactory, nameof(entityQueryableExpressionVisitorFactory));
            Check.NotNull(queryAnnotationExtractor, nameof(queryAnnotationExtractor));
            Check.NotNull(resultOperatorHandler, nameof(resultOperatorHandler));
            Check.NotNull(entityMaterializerSource, nameof(entityMaterializerSource));
            Check.NotNull(expressionPrinter, nameof(expressionPrinter));
            Check.NotNull(queryCompilationContext, nameof(queryCompilationContext));

            Model = model;

            _queryOptimizer = queryOptimizer;
            _navigationRewritingExpressionVisitorFactory = navigationRewritingExpressionVisitorFactory;
            _subQueryMemberPushDownExpressionVisitor = subQueryMemberPushDownExpressionVisitor;
            _querySourceTracingExpressionVisitorFactory = querySourceTracingExpressionVisitorFactory;
            _entityResultFindingExpressionVisitorFactory = entityResultFindingExpressionVisitorFactory;
            _taskBlockingExpressionVisitor = taskBlockingExpressionVisitor;
            _memberAccessBindingExpressionVisitorFactory = memberAccessBindingExpressionVisitorFactory;
            _projectionExpressionVisitorFactory = projectionExpressionVisitorFactory;
            _entityQueryableExpressionVisitorFactory = entityQueryableExpressionVisitorFactory;
            _queryAnnotationExtractor = queryAnnotationExtractor;
            _resultOperatorHandler = resultOperatorHandler;
            _entityMaterializerSource = entityMaterializerSource;
            _expressionPrinter = expressionPrinter;
            _queryCompilationContext = queryCompilationContext;

            LinqOperatorProvider = queryCompilationContext.LinqOperatorProvider;
        }

        public virtual Expression Expression
        {
            get { return _expression; }
            [param: NotNull]
            protected set
            {
                Check.NotNull(value, nameof(value));

                _expression = value;
            }
        }

        public virtual ParameterExpression CurrentParameter
        {
            get { return _currentParameter; }
            [param: NotNull]
            set
            {
                Check.NotNull(value, nameof(value));

                _currentParameter = value;
            }
        }

        public virtual QueryCompilationContext QueryCompilationContext => _queryCompilationContext;

        public virtual ILinqOperatorProvider LinqOperatorProvider { get; private set; }

        public virtual Func<QueryContext, IEnumerable<TResult>> CreateQueryExecutor<TResult>([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            using (QueryCompilationContext.Logger.BeginScopeImpl(this))
            {
                QueryCompilationContext.Logger
                    .LogVerbose(
                        CoreLoggingEventId.CompilingQueryModel,
                        () => CoreStrings.LogCompilingQueryModel(queryModel));

                _blockTaskExpressions = false;

                ExtractQueryAnnotations(queryModel);

                OptimizeQueryModel(queryModel);

                QueryCompilationContext.FindQuerySourcesRequiringMaterialization(this, queryModel);
                QueryCompilationContext.DetermineQueryBufferRequirement(queryModel);

                VisitQueryModel(queryModel);

                SingleResultToSequence(queryModel);

                IncludeNavigations(queryModel);

                TrackEntitiesInResults<TResult>(queryModel);

                InterceptExceptions();

                return CreateExecutorLambda<IEnumerable<TResult>>();
            }
        }

        public virtual Func<QueryContext, IAsyncEnumerable<TResult>> CreateAsyncQueryExecutor<TResult>([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            using (QueryCompilationContext.Logger.BeginScopeImpl(this))
            {
                QueryCompilationContext.Logger
                    .LogVerbose(
                        CoreLoggingEventId.CompilingQueryModel,
                        () => CoreStrings.LogCompilingQueryModel(queryModel));

                _blockTaskExpressions = false;

                ExtractQueryAnnotations(queryModel);

                OptimizeQueryModel(queryModel);

                QueryCompilationContext.FindQuerySourcesRequiringMaterialization(this, queryModel);
                QueryCompilationContext.DetermineQueryBufferRequirement(queryModel);

                VisitQueryModel(queryModel);

                AsyncSingleResultToSequence(queryModel);

                IncludeNavigations(queryModel);

                TrackEntitiesInResults<TResult>(queryModel);

                InterceptExceptions();

                return CreateExecutorLambda<IAsyncEnumerable<TResult>>();
            }
        }

        protected virtual void InterceptExceptions()
        {
            _expression
                = Expression.Call(
                    LinqOperatorProvider.InterceptExceptions
                        .MakeGenericMethod(_expression.Type.GetSequenceType()),
                    _expression,
                    Expression.Constant(QueryCompilationContext.ContextType),
                    Expression.Constant(QueryCompilationContext.Logger));
        }

        protected virtual void ExtractQueryAnnotations([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            QueryCompilationContext.QueryAnnotations
                = _queryAnnotationExtractor.ExtractQueryAnnotations(queryModel);
        }

        protected virtual void OptimizeQueryModel([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            _queryOptimizer.Optimize(QueryCompilationContext.QueryAnnotations, queryModel);

            _navigationRewritingExpressionVisitorFactory.Create(this)
                .Rewrite(queryModel);

            queryModel.TransformExpressions(_subQueryMemberPushDownExpressionVisitor.Visit);

            QueryCompilationContext.Logger
                .LogVerbose(
                    CoreLoggingEventId.OptimizedQueryModel,
                    () => CoreStrings.LogOptimizedQueryModel(queryModel));
        }

        protected virtual void SingleResultToSequence([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (!(queryModel.GetOutputDataInfo() is StreamedSequenceInfo))
            {
                _expression
                    = Expression.Call(
                        LinqOperatorProvider.ToSequence
                            .MakeGenericMethod(_expression.Type),
                        _expression);
            }
        }

        protected virtual void AsyncSingleResultToSequence([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (!(queryModel.GetOutputDataInfo() is StreamedSequenceInfo))
            {
                _expression
                    = Expression.Call(
                        _taskToSequence.MakeGenericMethod(
                            _expression.Type.GetTypeInfo().GenericTypeArguments[0]),
                        _expression);
            }
        }

        private static readonly MethodInfo _taskToSequence
            = typeof(EntityQueryModelVisitor)
                .GetTypeInfo().GetDeclaredMethod(nameof(TaskToSequence));

        [UsedImplicitly]
        internal static IAsyncEnumerable<T> TaskToSequence<T>(Task<T> task)
            => new TaskResultAsyncEnumerable<T>(task);

        protected virtual void IncludeNavigations([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            if (queryModel.GetOutputDataInfo() is StreamedScalarValueInfo)
            {
                return;
            }

            var includeSpecifications
                = QueryCompilationContext.QueryAnnotations
                    .OfType<IncludeResultOperator>()
                    .Select(includeResultOperator =>
                        {
                            var navigationPath
                                = BindNavigationPathMemberExpression(
                                    includeResultOperator.NavigationPropertyPath,
                                    (ps, _) =>
                                        {
                                            var properties = ps.ToArray();
                                            var navigations = properties.OfType<INavigation>().ToArray();

                                            if (properties.Length != navigations.Length)
                                            {
                                                throw new InvalidOperationException(
                                                    CoreStrings.IncludeNonBindableExpression(
                                                        includeResultOperator.NavigationPropertyPath));
                                            }

                                            return BindChainedNavigations(
                                                navigations,
                                                includeResultOperator.ChainedNavigationProperties)
                                                .ToArray();
                                        });

                            if (navigationPath == null)
                            {
                                throw new InvalidOperationException(
                                    CoreStrings.IncludeNonBindableExpression(
                                        includeResultOperator.NavigationPropertyPath));
                            }

                            return new
                            {
                                specification = new IncludeSpecification(includeResultOperator.QuerySource, navigationPath),
                                order = string.Concat(navigationPath.Select(n => n.IsCollection() ? "1" : "0"))
                            };
                        })
                    .OrderByDescending(e => e.order)
                    .ThenBy(e => e.specification.NavigationPath.First().IsDependentToPrincipal())
                    .Select(e => e.specification)
                    .ToList();

            IncludeNavigations(queryModel, includeSpecifications);
        }

        protected virtual void IncludeNavigations(
            [NotNull] QueryModel queryModel,
            [NotNull] IReadOnlyCollection<IncludeSpecification> includeSpecifications)
        {
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(includeSpecifications, nameof(includeSpecifications));

            foreach (var includeSpecification in includeSpecifications)
            {
                var resultQuerySourceReferenceExpression
                    = _querySourceTracingExpressionVisitorFactory
                        .Create()
                        .FindResultQuerySourceReferenceExpression(
                            queryModel.SelectClause.Selector,
                            includeSpecification.QuerySource);

                if (resultQuerySourceReferenceExpression != null)
                {
                    var accessorLambda
                        = AccessorFindingExpressionVisitor
                            .FindAccessorLambda(
                                resultQuerySourceReferenceExpression,
                                queryModel.SelectClause.Selector,
                                Expression.Parameter(queryModel.SelectClause.Selector.Type, "result"));

                    QueryCompilationContext.Logger
                        .LogVerbose(
                            CoreLoggingEventId.IncludingNavigation,
                            () => CoreStrings.LogIncludingNavigation(includeSpecification.NavigationPath.Join(".")));

                    IncludeNavigations(
                        includeSpecification,
                        _expression.Type.GetSequenceType(),
                        accessorLambda,
                        QueryCompilationContext.IsTrackingQuery);

                    QueryCompilationContext
                        .AddTrackableInclude(
                            resultQuerySourceReferenceExpression.ReferencedQuerySource,
                            includeSpecification.NavigationPath);
                }
            }
        }

        private IEnumerable<INavigation> BindChainedNavigations(
            IEnumerable<INavigation> boundNavigations, IReadOnlyList<PropertyInfo> chainedNavigationProperties)
        {
            var boundChainedNavigations = new List<INavigation>();

            if (chainedNavigationProperties != null)
            {
                foreach (
                    var navigation in
                        from propertyInfo in chainedNavigationProperties
                        // ReSharper disable once AssignNullToNotNullAttribute
                        let entityType = Model.FindEntityType(propertyInfo.DeclaringType)
                        select entityType?.FindNavigation(propertyInfo.Name))
                {
                    if (navigation == null)
                    {
                        return null;
                    }

                    boundChainedNavigations.Add(navigation);
                }
            }

            return boundNavigations.Concat(boundChainedNavigations);
        }

        protected virtual void IncludeNavigations(
            [NotNull] IncludeSpecification includeSpecification,
            [NotNull] Type resultType,
            [NotNull] LambdaExpression accessorLambda,
            bool querySourceRequiresTracking)
        {
            // template method

            throw new NotImplementedException(CoreStrings.IncludeNotImplemented);
        }

        protected virtual void TrackEntitiesInResults<TResult>(
            [NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            var lastTrackingModifier
                = QueryCompilationContext.QueryAnnotations
                    .OfType<TrackingResultOperator>()
                    .LastOrDefault();

            if (queryModel.GetOutputDataInfo() is StreamedScalarValueInfo
                || (!QueryCompilationContext.TrackQueryResults
                    && (lastTrackingModifier == null))
                || ((lastTrackingModifier != null)
                    && !lastTrackingModifier.IsTracking))
            {
                return;
            }

            var entityTrackingInfos
                = _entityResultFindingExpressionVisitorFactory.Create(QueryCompilationContext)
                    .FindEntitiesInResult(queryModel.SelectClause.Selector);

            if (entityTrackingInfos.Any())
            {
                var resultItemType = _expression.Type.GetSequenceType();
                var resultItemTypeInfo = resultItemType.GetTypeInfo();

                MethodInfo trackingMethod;

                if (resultItemTypeInfo.IsGenericType
                    && ((resultItemTypeInfo.GetGenericTypeDefinition() == typeof(IGrouping<,>))
                        || (resultItemTypeInfo.GetGenericTypeDefinition() == typeof(IAsyncGrouping<,>))))
                {
                    trackingMethod
                        = LinqOperatorProvider.TrackGroupedEntities
                            .MakeGenericMethod(
                                resultItemType.GenericTypeArguments[0],
                                resultItemType.GenericTypeArguments[1],
                                queryModel.SelectClause.Selector.Type);
                }
                else
                {
                    trackingMethod
                        = LinqOperatorProvider.TrackEntities
                            .MakeGenericMethod(
                                resultItemType,
                                queryModel.SelectClause.Selector.Type);
                }

                _expression
                    = Expression.Call(
                        trackingMethod,
                        _expression,
                        QueryContextParameter,
                        Expression.Constant(entityTrackingInfos),
                        Expression.Constant(
                            _getEntityAccessors
                                .MakeGenericMethod(queryModel.SelectClause.Selector.Type)
                                .Invoke(
                                    null,
                                    new object[]
                                    {
                                        entityTrackingInfos,
                                        queryModel.SelectClause.Selector
                                    })));
            }
        }

        private static readonly MethodInfo _getEntityAccessors
            = typeof(EntityQueryModelVisitor)
                .GetTypeInfo().GetDeclaredMethod(nameof(GetEntityAccessors));

        [UsedImplicitly]
        private static ICollection<Func<TResult, object>> GetEntityAccessors<TResult>(
            IEnumerable<EntityTrackingInfo> entityTrackingInfos,
            Expression selector)
            => (from entityTrackingInfo in entityTrackingInfos
                select
                    (Func<TResult, object>)
                        AccessorFindingExpressionVisitor
                            .FindAccessorLambda(
                                entityTrackingInfo.QuerySourceReferenceExpression,
                                selector,
                                Expression.Parameter(typeof(TResult), "result"))
                            .Compile())
                .ToList();

        protected virtual Func<QueryContext, TResults> CreateExecutorLambda<TResults>()
        {
            var queryExecutorExpression
                = Expression
                    .Lambda<Func<QueryContext, TResults>>(
                        _expression, QueryContextParameter);

            var queryExecutor = queryExecutorExpression.Compile();

            QueryCompilationContext.Logger.LogVerbose(
                CoreLoggingEventId.QueryPlan,
                () =>
                    {
                        var queryPlan = _expressionPrinter.Print(queryExecutorExpression);

                        return queryPlan;
                    });

            return queryExecutor;
        }

        public override void VisitQueryModel([NotNull] QueryModel queryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            base.VisitQueryModel(queryModel);

            if (_blockTaskExpressions)
            {
                _expression
                    = _taskBlockingExpressionVisitor
                        .Visit(_expression);
            }
        }

        public override void VisitMainFromClause(
            [NotNull] MainFromClause fromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            _expression = CompileMainFromClauseExpression(fromClause, queryModel);

            if (LinqOperatorProvider is AsyncLinqOperatorProvider
                && _expression.Type.TryGetElementType(typeof(IEnumerable<>)) != null)
            {
                LinqOperatorProvider = new LinqOperatorProvider();
            }

            CurrentParameter
                = Expression.Parameter(
                    _expression.Type.GetSequenceType(),
                    fromClause.ItemName);

            AddOrUpdateMapping(fromClause, CurrentParameter);
        }

        protected virtual Expression CompileMainFromClauseExpression(
            [NotNull] MainFromClause mainFromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(mainFromClause, nameof(mainFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(mainFromClause.FromExpression, mainFromClause);
        }

        public override void VisitAdditionalFromClause(
            [NotNull] AdditionalFromClause fromClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var fromExpression
                = CompileAdditionalFromClauseExpression(fromClause, queryModel);

            var innerItemParameter
                = Expression.Parameter(
                    fromExpression.Type.GetSequenceType(), fromClause.ItemName);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.SelectMany
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            transparentIdentifierType),
                    _expression,
                    Expression.Lambda(fromExpression, CurrentParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType, CurrentParameter, innerItemParameter),
                        CurrentParameter,
                        innerItemParameter));

            IntroduceTransparentScope(fromClause, queryModel, index, transparentIdentifierType);
        }

        protected virtual Expression CompileAdditionalFromClauseExpression(
            [NotNull] AdditionalFromClause additionalFromClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(additionalFromClause, nameof(additionalFromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(additionalFromClause.FromExpression, additionalFromClause);
        }

        public override void VisitJoinClause(
            [NotNull] JoinClause joinClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var outerKeySelectorExpression
                = ReplaceClauseReferences(joinClause.OuterKeySelector, joinClause);

            var innerSequenceExpression
                = CompileJoinClauseInnerSequenceExpression(joinClause, queryModel);

            var innerItemParameter
                = Expression.Parameter(
                    innerSequenceExpression.Type.GetSequenceType(), joinClause.ItemName);

            if (!_queryCompilationContext.QuerySourceMapping.ContainsMapping(joinClause))
            {
                _queryCompilationContext.QuerySourceMapping
                    .AddMapping(joinClause, innerItemParameter);
            }

            var innerKeySelectorExpression
                = ReplaceClauseReferences(joinClause.InnerKeySelector, joinClause);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.Join
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            outerKeySelectorExpression.Type,
                            transparentIdentifierType),
                    _expression,
                    innerSequenceExpression,
                    Expression.Lambda(outerKeySelectorExpression, CurrentParameter),
                    Expression.Lambda(innerKeySelectorExpression, innerItemParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType,
                            CurrentParameter,
                            innerItemParameter),
                        CurrentParameter,
                        innerItemParameter));

            IntroduceTransparentScope(joinClause, queryModel, index, transparentIdentifierType);
        }

        protected virtual Expression CompileJoinClauseInnerSequenceExpression(
            [NotNull] JoinClause joinClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(joinClause.InnerSequence, joinClause);
        }

        public override void VisitGroupJoinClause(
            [NotNull] GroupJoinClause groupJoinClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var outerKeySelectorExpression
                = ReplaceClauseReferences(groupJoinClause.JoinClause.OuterKeySelector, groupJoinClause);

            var innerSequenceExpression
                = CompileGroupJoinInnerSequenceExpression(groupJoinClause, queryModel);

            var innerItemParameter
                = Expression.Parameter(
                    innerSequenceExpression.Type.GetSequenceType(),
                    groupJoinClause.JoinClause.ItemName);

            _queryCompilationContext.QuerySourceMapping
                .AddMapping(groupJoinClause.JoinClause, innerItemParameter);

            var innerKeySelectorExpression
                = ReplaceClauseReferences(groupJoinClause.JoinClause.InnerKeySelector, groupJoinClause);

            var innerItemsParameter
                = Expression.Parameter(
                    LinqOperatorProvider.MakeSequenceType(innerItemParameter.Type),
                    groupJoinClause.ItemName);

            var transparentIdentifierType
                = typeof(TransparentIdentifier<,>)
                    .MakeGenericType(CurrentParameter.Type, innerItemsParameter.Type);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.GroupJoin
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            innerItemParameter.Type,
                            outerKeySelectorExpression.Type,
                            transparentIdentifierType),
                    _expression,
                    innerSequenceExpression,
                    Expression.Lambda(outerKeySelectorExpression, CurrentParameter),
                    Expression.Lambda(innerKeySelectorExpression, innerItemParameter),
                    Expression.Lambda(
                        CallCreateTransparentIdentifier(
                            transparentIdentifierType,
                            CurrentParameter,
                            innerItemsParameter),
                        CurrentParameter,
                        innerItemsParameter));

            IntroduceTransparentScope(groupJoinClause, queryModel, index, transparentIdentifierType);
        }

        protected virtual Expression CompileGroupJoinInnerSequenceExpression(
            [NotNull] GroupJoinClause groupJoinClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            return ReplaceClauseReferences(groupJoinClause.JoinClause.InnerSequence, groupJoinClause.JoinClause);
        }

        public override void VisitWhereClause(
            [NotNull] WhereClause whereClause, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(whereClause, nameof(whereClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var predicate = ReplaceClauseReferences(whereClause.Predicate);

            _expression
                = Expression.Call(
                    LinqOperatorProvider.Where.MakeGenericMethod(CurrentParameter.Type),
                    _expression,
                    Expression.Lambda(predicate, CurrentParameter));
        }

        public override void VisitOrdering(
            [NotNull] Ordering ordering,
            [NotNull] QueryModel queryModel,
            [NotNull] OrderByClause orderByClause,
            int index)
        {
            Check.NotNull(ordering, nameof(ordering));
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(orderByClause, nameof(orderByClause));

            var expression = ReplaceClauseReferences(ordering.Expression);

            _expression
                = Expression.Call(
                    (index == 0
                        ? LinqOperatorProvider.OrderBy
                        : LinqOperatorProvider.ThenBy)
                        .MakeGenericMethod(CurrentParameter.Type, expression.Type),
                    _expression,
                    Expression.Lambda(expression, CurrentParameter),
                    Expression.Constant(ordering.OrderingDirection));
        }

        public override void VisitSelectClause(
            [NotNull] SelectClause selectClause, [NotNull] QueryModel queryModel)
        {
            Check.NotNull(selectClause, nameof(selectClause));
            Check.NotNull(queryModel, nameof(queryModel));

            var sequenceType = _expression.Type.GetSequenceType();

            if (selectClause.Selector.Type == sequenceType)
            {
                return;
            }

            var selector
                = ReplaceClauseReferences(
                    _projectionExpressionVisitorFactory
                        .Create(this, queryModel.MainFromClause)
                        .Visit(selectClause.Selector),
                    inProjection: true);

            if (selector.Type != sequenceType)
            {
                _expression
                    = Expression.Call(
                        LinqOperatorProvider.Select
                            .MakeGenericMethod(CurrentParameter.Type, selector.Type),
                        _expression,
                        Expression.Lambda(selector, CurrentParameter));
            }
        }

        public override void VisitResultOperator(
            [NotNull] ResultOperatorBase resultOperator, [NotNull] QueryModel queryModel, int index)
        {
            Check.NotNull(resultOperator, nameof(resultOperator));
            Check.NotNull(queryModel, nameof(queryModel));

            _expression
                = _resultOperatorHandler
                    .HandleResultOperator(this, resultOperator, queryModel);
        }

        #region Transparent Identifiers

        public const string CreateTransparentIdentifierMethodName = "CreateTransparentIdentifier";

        private struct TransparentIdentifier<TOuter, TInner>
        {
            [UsedImplicitly]
            public static TransparentIdentifier<TOuter, TInner> CreateTransparentIdentifier(TOuter outer, TInner inner)
                => new TransparentIdentifier<TOuter, TInner>(outer, inner);

            private TransparentIdentifier(TOuter outer, TInner inner)
            {
                Outer = outer;
                Inner = inner;
            }

            [UsedImplicitly]
            public TOuter Outer;

            [UsedImplicitly]
            public TInner Inner;
        }

        private static Expression CallCreateTransparentIdentifier(
            Type transparentIdentifierType, Expression outerExpression, Expression innerExpression)
        {
            var createTransparentIdentifierMethodInfo
                = transparentIdentifierType.GetTypeInfo().GetDeclaredMethod(CreateTransparentIdentifierMethodName);

            return Expression.Call(createTransparentIdentifierMethodInfo, outerExpression, innerExpression);
        }

        private static Expression AccessOuterTransparentField(
            Type transparentIdentifierType, Expression targetExpression)
        {
            var fieldInfo = transparentIdentifierType.GetTypeInfo().GetDeclaredField("Outer");

            return Expression.Field(targetExpression, fieldInfo);
        }

        private static Expression AccessInnerTransparentField(
            Type transparentIdentifierType, Expression targetExpression)
        {
            var fieldInfo = transparentIdentifierType.GetTypeInfo().GetDeclaredField("Inner");

            return Expression.Field(targetExpression, fieldInfo);
        }

        private void IntroduceTransparentScope(
            IQuerySource fromClause, QueryModel queryModel, int index, Type transparentIdentifierType)
        {
            CurrentParameter
                = Expression.Parameter(transparentIdentifierType, $"t{_transparentParameterCounter++}");

            var outerAccessExpression
                = AccessOuterTransparentField(transparentIdentifierType, CurrentParameter);

            RescopeTransparentAccess(queryModel.MainFromClause, outerAccessExpression);

            for (var i = 0; i < index; i++)
            {
                var querySource = queryModel.BodyClauses[i] as IQuerySource;

                if (querySource != null)
                {
                    RescopeTransparentAccess(querySource, outerAccessExpression);
                }
            }

            AddOrUpdateMapping(fromClause, AccessInnerTransparentField(transparentIdentifierType, CurrentParameter));
        }

        private void RescopeTransparentAccess(IQuerySource querySource, Expression targetExpression)
        {
            var memberAccessExpression
                = ShiftMemberAccess(
                    targetExpression,
                    _queryCompilationContext.QuerySourceMapping.GetExpression(querySource));

            _queryCompilationContext.QuerySourceMapping.ReplaceMapping(querySource, memberAccessExpression);
        }

        private static Expression ShiftMemberAccess(Expression targetExpression, Expression currentExpression)
        {
            var memberExpression = currentExpression as MemberExpression;

            if (memberExpression == null)
            {
                return targetExpression;
            }

            return Expression.MakeMemberAccess(
                ShiftMemberAccess(targetExpression, memberExpression.Expression),
                memberExpression.Member);
        }

        #endregion

        public virtual Expression ReplaceClauseReferences(
            [NotNull] Expression expression,
            [CanBeNull] IQuerySource querySource = null,
            bool inProjection = false)
        {
            Check.NotNull(expression, nameof(expression));

            expression
                = _entityQueryableExpressionVisitorFactory
                    .Create(this, querySource)
                    .Visit(expression);

            expression
                = _memberAccessBindingExpressionVisitorFactory
                    .Create(QueryCompilationContext.QuerySourceMapping, this, inProjection)
                    .Visit(expression);

            if (!inProjection
                && (expression.Type != typeof(string)
                    && expression.Type != typeof(byte[]))
                && _expression?.Type.TryGetElementType(typeof(IAsyncEnumerable<>)) != null)
            {
                var elementType = expression.Type.TryGetElementType(typeof(IEnumerable<>));

                if (elementType != null)
                {
                    return
                        Expression.Call(
                            AsyncLinqOperatorProvider
                                .ToAsyncEnumerableMethod
                                .MakeGenericMethod(elementType),
                            expression);
                }
            }

            return expression;
        }

        public virtual void AddOrUpdateMapping(
            [NotNull] IQuerySource querySource, [NotNull] Expression expression)
        {
            Check.NotNull(querySource, nameof(querySource));
            Check.NotNull(expression, nameof(expression));

            if (!_queryCompilationContext.QuerySourceMapping.ContainsMapping(querySource))
            {
                _queryCompilationContext.QuerySourceMapping.AddMapping(querySource, expression);
            }
            else
            {
                _queryCompilationContext.QuerySourceMapping.ReplaceMapping(querySource, expression);
            }
        }

        #region Binding

        public virtual Expression BindMethodCallToValueBuffer(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Expression expression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(expression, nameof(expression));

            return BindMethodCallExpression(
                methodCallExpression,
                (property, querySource)
                    => BindReadValueMethod(methodCallExpression.Type, expression, property.GetIndex()));
        }

        public virtual Expression BindMemberToValueBuffer(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Expression expression)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(expression, nameof(expression));

            return BindMemberExpression(
                memberExpression,
                null,
                (property, querySource)
                    => BindReadValueMethod(memberExpression.Type, expression, property.GetIndex()));
        }

        public virtual Expression BindReadValueMethod(
            [NotNull] Type memberType,
            [NotNull] Expression expression,
            int index)
        {
            Check.NotNull(memberType, nameof(memberType));
            Check.NotNull(expression, nameof(expression));

            return _entityMaterializerSource
                .CreateReadValueExpression(expression, memberType, index);
        }

        public virtual TResult BindNavigationPathMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Func<IEnumerable<IPropertyBase>, IQuerySource, TResult> memberBinder)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return BindMemberExpressionCore(memberExpression, null, memberBinder);
        }

        public virtual void BindMemberExpression(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Action<IProperty, IQuerySource> memberBinder)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            BindMemberExpression(memberExpression, null,
                (property, querySource) =>
                    {
                        memberBinder(property, querySource);

                        return default(object);
                    });
        }

        public virtual TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [CanBeNull] IQuerySource querySource,
            [NotNull] Func<IProperty, IQuerySource, TResult> memberBinder)
        {
            Check.NotNull(memberExpression, nameof(memberExpression));
            Check.NotNull(memberBinder, nameof(memberBinder));

            return BindMemberExpressionCore(memberExpression, querySource,
                (ps, qs) =>
                    {
                        var property = ps.Single() as IProperty;

                        return property != null
                            ? memberBinder(property, qs)
                            : default(TResult);
                    });
        }

        private TResult BindMemberExpressionCore<TResult>(
            MemberExpression memberExpression,
            IQuerySource querySource,
            Func<IEnumerable<IPropertyBase>, IQuerySource, TResult> memberBinder)
        {
            QuerySourceReferenceExpression querySourceReferenceExpression;

            var properties
                = IterateCompositeMemberExpression(memberExpression, out querySourceReferenceExpression);

            if ((querySourceReferenceExpression != null)
                && ((querySource == null)
                    || (querySource == querySourceReferenceExpression.ReferencedQuerySource)))
            {
                return memberBinder(
                    properties,
                    querySourceReferenceExpression.ReferencedQuerySource);
            }

            return default(TResult);
        }

        private IEnumerable<IPropertyBase> IterateCompositeMemberExpression(
            MemberExpression memberExpression, out QuerySourceReferenceExpression querySourceReferenceExpression)
        {
            querySourceReferenceExpression = null;

            var properties = new List<IPropertyBase>();

            while (memberExpression?.Expression != null)
            {
                var expression = memberExpression.Expression;

                var entityType = Model.FindEntityType(expression.Type);

                expression = expression.RemoveConvert();

                if (entityType == null)
                {
                    entityType = Model.FindEntityType(expression.Type);

                    if (entityType == null)
                    {
                        break;
                    }
                }

                var property
                    = (IPropertyBase)entityType.FindProperty(memberExpression.Member.Name)
                      ?? entityType.FindNavigation(memberExpression.Member.Name);

                if (property == null)
                {
                    break;
                }

                properties.Add(property);

                querySourceReferenceExpression = expression as QuerySourceReferenceExpression;
                memberExpression = expression as MemberExpression;
            }

            return querySourceReferenceExpression != null
                ? Enumerable.Reverse(properties)
                : Enumerable.Empty<IPropertyBase>();
        }

        public virtual TResult BindMethodCallExpression<TResult>(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Func<IProperty, IQuerySource, TResult> methodCallBinder)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(methodCallBinder, nameof(methodCallBinder));

            return BindMethodCallExpression(methodCallExpression, null, methodCallBinder);
        }

        public virtual void BindMethodCallExpression(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Action<IProperty, IQuerySource> methodCallBinder)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(methodCallBinder, nameof(methodCallBinder));

            BindMethodCallExpression(methodCallExpression, null,
                (property, querySource) =>
                    {
                        methodCallBinder(property, querySource);

                        return default(object);
                    });
        }

        public virtual TResult BindMethodCallExpression<TResult>(
            [NotNull] MethodCallExpression methodCallExpression,
            [CanBeNull] IQuerySource querySource,
            [NotNull] Func<IProperty, IQuerySource, TResult> methodCallBinder)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));
            Check.NotNull(methodCallBinder, nameof(methodCallBinder));

            if (methodCallExpression.Method.IsGenericMethod)
            {
                var methodInfo = methodCallExpression.Method.GetGenericMethodDefinition();

                if (ReferenceEquals(methodInfo, PropertyMethodInfo))
                {
                    var targetExpression = methodCallExpression.Arguments[0];

                    MemberExpression memberExpression;
                    while ((memberExpression = targetExpression as MemberExpression) != null)
                    {
                        targetExpression = memberExpression.Expression;
                    }

                    var querySourceReferenceExpression
                        = targetExpression as QuerySourceReferenceExpression;

                    if ((querySourceReferenceExpression == null)
                        || (querySource == null)
                        || (querySource == querySourceReferenceExpression.ReferencedQuerySource))
                    {
                        var entityType = Model.FindEntityType(methodCallExpression.Arguments[0].Type);

                        if (entityType != null)
                        {
                            var propertyName = (string)((ConstantExpression)methodCallExpression.Arguments[1]).Value;
                            var property = entityType.FindProperty(propertyName);

                            if (property != null)
                            {
                                return methodCallBinder(
                                    property,
                                    querySourceReferenceExpression?.ReferencedQuerySource);
                            }
                        }
                    }
                }
            }

            return default(TResult);
        }

        #endregion
    }
}
