﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Transactions;
using EventSourcing.Domain;

namespace EventSourcing.Infrastructure
{
    public class UnitOfWork : IUnitOfWork//, IEnlistmentNotification
    {
        private readonly Dictionary<Guid, IAggregateRoot> _identityMap;
        private readonly IEventStore _eventStore;
        private readonly IAggregateBuilder _aggregateBuilder;
        private readonly IEventHandlerFactory _eventHandlerFactory;

        public UnitOfWork(IEventStore eventStore, IAggregateBuilder aggregateBuilder, IEventHandlerFactory eventHandlerFactory)
        {
            _eventStore = eventStore;
            _aggregateBuilder = aggregateBuilder;
            _eventHandlerFactory = eventHandlerFactory;
            _identityMap = new Dictionary<Guid, IAggregateRoot>();
        }

        public TAggregateRoot GetById<TAggregateRoot>(Guid id) where TAggregateRoot : class, IAggregateRoot
        {
            if (_identityMap.ContainsKey(id))
            {
                return (TAggregateRoot)_identityMap[id];
            }

            return Load<TAggregateRoot>(id);
        }

        private TAggregateRoot Load<TAggregateRoot>(Guid id) where TAggregateRoot : class, IAggregateRoot
        {
            var isSnapshotable = typeof(ISnapshotProvider).IsAssignableFrom(typeof(TAggregateRoot));
            var snapshot =  isSnapshotable ? _eventStore.LoadSnapshot<ISnapshot>(id) : null;

            TAggregateRoot ar;
            if(snapshot != null)
            {
                var events = _eventStore.Replay<IDomainEvent>(id, snapshot.Version + 1);
                ar = _aggregateBuilder.BuildFromSnapshot<TAggregateRoot>(snapshot, events);
            }
            else
            {
                var events = _eventStore.Replay<IDomainEvent>(id);
                ar = _aggregateBuilder.BuildFromEventStream<TAggregateRoot>(events);
            }

            if(ar != null)
            {
                _identityMap.Add(id,ar);
            }
            return ar;
        }

        public void Add<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : class, IAggregateRoot
        {
            _identityMap.Add(aggregateRoot.Id, aggregateRoot);
        }

        public void Commit()
        {
            _identityMap.Values.ForEach(CommitAggregateRoot);
            _identityMap.Clear();
        }

        private void CommitAggregateRoot(IAggregateRoot aggregateRoot)
        {
            var eventsToCommit = aggregateRoot.FlushEvents();

            if (eventsToCommit.Count() == 0)
            {
                return;
            }

            _eventStore.StoreEvents(aggregateRoot.Id, eventsToCommit);

            StoreSnapshot(aggregateRoot);
            RaiseEvents(eventsToCommit);
        }

        private void RaiseEvents(IEnumerable<IDomainEvent> domainEvents)
        {
            var callHandlersMethodInfo = GetType().GetMethod("CallHandlers", BindingFlags.Instance | BindingFlags.NonPublic);

            var typeMap = new Dictionary<Type, MethodInfo>();

            foreach (var @event in domainEvents)
            {
                MethodInfo methodInfo;
                if(!typeMap.TryGetValue(@event.GetType(), out methodInfo))
                {
                    methodInfo = callHandlersMethodInfo.MakeGenericMethod(@event.GetType());
                    typeMap.Add(@event.GetType(),methodInfo);
                }
                methodInfo.Invoke(this, new[] {@event});
            }
        }

        // called through reflection - DON'T DELETE ME JUST BECAUSE RESHARPER TELLS YOU I AM NOT USED
        private void CallHandlers<TDomainEvent>(TDomainEvent domainEvent) where TDomainEvent : IDomainEvent
        {
            var handlers = _eventHandlerFactory.ResolveHandlers<TDomainEvent>();
            handlers.ForEach(x => x.Handle(domainEvent));
        }

        private void StoreSnapshot(IAggregateRoot aggregateRoot)
        {
            var snapshotProvider = aggregateRoot as ISnapshotProvider;

            if (snapshotProvider == null || aggregateRoot.Version % snapshotProvider.SnapshotInterval != 0)
            {
                return;
            }

            var snapshot = snapshotProvider.Snapshot();
            _eventStore.StoreSnapshot(aggregateRoot.Id, snapshot);
        }
    }
}