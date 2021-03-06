﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NHibernate.Collection;
using NHibernate.Engine;
using NHibernate.Extensions.Helpers;
using NHibernate.Extensions.Internal;
using NHibernate.Impl;
using NHibernate.Persister.Entity;
using NHibernate.Proxy;
using NHibernate.Type;

namespace NHibernate.Extensions
{
    public static class DeepCloneSessionExtensions
    {
        private static readonly PropertyInfo IsAlreadyDisposedPropInfo;
        private static readonly MethodInfo StatelessSessionGetMethod;

        static DeepCloneSessionExtensions()
        {
            IsAlreadyDisposedPropInfo = typeof (SessionImpl).GetProperty("IsAlreadyDisposed", BindingFlags.Instance | BindingFlags.NonPublic);
            StatelessSessionGetMethod = typeof (IStatelessSession).GetMethods()
                .First(o => o.Name == "Get" && o.IsGenericMethod && o.GetParameters().Length == 1);
        }

        #region ISession DeepClone

        public static IList<T> DeepClone<T>(this ISession session, IEnumerable<T> entities, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            var resolvedEntities = new Dictionary<object, object>();
            return entities.Select(entity => (T)DeepClone(session.GetSessionImplementation(), entity, opts, null, resolvedEntities)).ToList();
        }

        public static T DeepClone<T>(this ISession session, T entity, Func<DeepCloneOptions, DeepCloneOptions> optsAction = null)
        {
            var opts = new DeepCloneOptions();
            if (optsAction != null)
                opts = optsAction(opts);
            return (T)DeepClone(session.GetSessionImplementation(), entity, opts, null, new Dictionary<object, object>());
        }

        public static object DeepClone(this ISession session, object entity, System.Type entityType = null, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            return DeepClone(session.GetSessionImplementation(), entity, opts, entityType, new Dictionary<object, object>());
        }

        public static IEnumerable DeepClone(this ISession session, IEnumerable entities, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            var collection = (IEnumerable)CreateNewCollection(entities.GetType());
            var resolvedEntities = new Dictionary<object, object>();
            foreach (var entity in entities)
            {
                AddItemToCollection(collection, DeepClone(session.GetSessionImplementation(), entity, opts, null, resolvedEntities));
            }
            return collection;
        }

        #endregion

        #region IStatelessSession DeepClone

        public static IList<T> DeepClone<T>(this IStatelessSession session, IEnumerable<T> entities, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            var resolvedEntities = new Dictionary<object, object>();
            return entities.Select(entity => (T)DeepClone(session.GetSessionImplementation(), entity, opts, null, resolvedEntities)).ToList();
        }


        public static T DeepClone<T>(this IStatelessSession session, T entity, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            return (T)DeepClone(session.GetSessionImplementation(), entity, opts, null, new Dictionary<object, object>());
        }


        public static object DeepClone(this IStatelessSession session, object entity, System.Type entityType = null, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            return DeepClone(session.GetSessionImplementation(), entity, opts, entityType, new Dictionary<object, object>());
        }


        public static IEnumerable DeepClone(this IStatelessSession session, IEnumerable entities, Func<DeepCloneOptions, DeepCloneOptions> optsFn = null)
        {
            var opts = new DeepCloneOptions();
            if (optsFn != null)
                opts = optsFn(opts);
            var collection = (IEnumerable)CreateNewCollection(entities.GetType());
            var resolvedEntities = new Dictionary<object, object>();
            foreach (var entity in entities)
            {
                AddItemToCollection(collection, DeepClone(session.GetSessionImplementation(), entity, opts, null, resolvedEntities));
            }
            return collection;
        }

        #endregion

        private static object CopyOnlyForeignKeyProperties(object entity, System.Type entityType,
            AbstractEntityPersister entityMetadata, DeepCloneOptions opts, DeepCloneParentEntity parentEntity)
        {
            var propertyInfos = entityType.GetProperties();

            //Copy only Fks
            foreach (var propertyInfo in propertyInfos
                .Where(p => opts.CanCloneIdentifier(entityType) || entityMetadata.IdentifierPropertyName != p.Name)
                .Where(p => !opts.GetIgnoreMembers(entityType).Contains(p.Name))
                .Where(p => p.GetSetMethod(true) != null))
            {
                IType entityPropertyType;
                try
                {
                    entityPropertyType = entityMetadata.GetPropertyType(propertyInfo.Name);
                }
                catch (Exception)
                {
                    continue;
                }
                if (!NHibernateUtil.IsPropertyInitialized(entity, propertyInfo.Name))
                    continue;
                var propertyValue = propertyInfo.GetValue(entity, null);
                if (!NHibernateUtil.IsInitialized(propertyValue))
                    continue;

                var colNames = entityMetadata.GetPropertyColumnNames(propertyInfo.Name);
                if (!entityPropertyType.IsEntityType) continue;
                //Check if we have a parent entity and that is bidirectional related to the current property (one-to-many)
                if (parentEntity.ReferencedColumns.SequenceEqual(colNames))
                {
                    propertyInfo.SetValue(entity, parentEntity.Entity, null);
                }
            }
            return entity;
        }

        private static object LoadEntity(ISessionImplementor sessionImpl, System.Type type, object identifier)
        {
            var statelessSession = sessionImpl as IStatelessSession;
            if (statelessSession != null)
                return StatelessSessionGetMethod.MakeGenericMethod(type).Invoke(statelessSession, new[] { identifier });
            var session = sessionImpl as ISession;
            return session != null ? session.Load(type, identifier) : null;
        }

        private static object DeepClone(this ISessionImplementor session, object entity, DeepCloneOptions opts, System.Type entityType,
            IDictionary<object, object> resolvedEntities, DeepCloneParentEntity parentEntity = null)
        {
            opts = opts ?? new DeepCloneOptions();
            if (entity == null)
                return entityType.GetDefaultValue();
            entityType = entityType ?? entity.GetUnproxiedType();

            AbstractEntityPersister entityMetadata;
            try
            {
                entityMetadata = (AbstractEntityPersister)session.Factory.GetClassMetadata(entityType);
            }
            catch (Exception)
            {
                return entityType.GetDefaultValue();
            }

            if (!NHibernateUtil.IsInitialized(entity))
                return entityType.GetDefaultValue();

            if (resolvedEntities.ContainsKey(entity) && parentEntity != null)
                return CopyOnlyForeignKeyProperties(resolvedEntities[entity], entityType, entityMetadata, opts, parentEntity);

            if (resolvedEntities.ContainsKey(entity))
                return resolvedEntities[entity];

            if (opts.CanCloneAsReferenceFunc != null && opts.CanCloneAsReferenceFunc(entityType))
                return entity;

            var propertyInfos = entityType.GetProperties();
            var copiedEntity = Activator.CreateInstance(entityType);
            resolvedEntities.Add(entity, copiedEntity);

            foreach (var propertyInfo in propertyInfos
                .Where(p => opts.CanCloneIdentifier(entityType) || entityMetadata.IdentifierPropertyName != p.Name)
                .Where(p => !opts.GetIgnoreMembers(entityType).Contains(p.Name))
                .Where(p => p.GetSetMethod(true) != null))
            {
                IType entityPropertyType;
                try
                {
                    entityPropertyType = entityMetadata.GetPropertyType(propertyInfo.Name);
                }
                catch (Exception)
                {
                    continue;
                }

                var resolveFn = opts.GetResolveFunction(entityType, propertyInfo.Name);
                if (resolveFn != null)
                {
                    propertyInfo.SetValue(copiedEntity, resolveFn(entity), null);
                    continue;
                }

                if (entityPropertyType.IsEntityType && opts.SkipEntityTypesValue.HasValue && opts.SkipEntityTypesValue.Value)
                    continue;

                //TODO: verify: false only when entity is a proxy or lazy field/property that is not yet initialized
                if (!NHibernateUtil.IsPropertyInitialized(entity, propertyInfo.Name))
                    continue;

                var propertyValue = propertyInfo.GetValue(entity, null);
                if (!NHibernateUtil.IsInitialized(propertyValue))
                {
                    //Use session load for proxy, works only for references (collections are not supported) 
                    if (
                        propertyValue != null &&
                        propertyValue.IsProxy() &&
                        !(propertyValue is IPersistentCollection) &&
                        opts.UseSessionLoadFunction
                        )
                    {
                        var lazyInit = ((INHibernateProxy)propertyValue).HibernateLazyInitializer;
                        propertyInfo.SetValue(copiedEntity, LoadEntity(session, lazyInit.PersistentClass, lazyInit.Identifier), null);
                    }
                    continue;
                }

                var colNames = entityMetadata.GetPropertyColumnNames(propertyInfo.Name);
                var propType = propertyInfo.PropertyType;
                var copyAsReference = opts.CanCloneAsReference(entityType, propertyInfo.Name);
                if (entityPropertyType.IsCollectionType)
                {
                    var propertyList = CreateNewCollection(propType);
                    propertyInfo.SetValue(copiedEntity, propertyList, null);
                    AddItemToCollection(propertyList, propertyValue, o => copyAsReference
                        ? o
                        : session.DeepClone(o, opts, o.GetUnproxiedType(), resolvedEntities,
                            new DeepCloneParentEntity
                            {
                                Entity = copiedEntity,
                                EntityPersister = entityMetadata,
                                ChildType = entityPropertyType,
                                ReferencedColumns = ((CollectionType)entityPropertyType)
                                    .GetReferencedColumns(session.Factory)
                            }));
                }
                else if (entityPropertyType.IsEntityType)
                {
                    if (copyAsReference)
                        propertyInfo.SetValue(copiedEntity, propertyValue, null);
                    //Check if we have a parent entity and that is bidirectional related to the current property (one-to-many)
                    else if (parentEntity != null && parentEntity.ReferencedColumns.SequenceEqual(colNames))
                        propertyInfo.SetValue(copiedEntity, parentEntity.Entity, null);
                    else
                        propertyInfo.SetValue(copiedEntity, session.DeepClone(propertyValue, opts, propType, resolvedEntities), null);
                }
                else if (propType.IsSimpleType())
                {
                    //Check if we have a parent entity and that is bidirectional related to the current property (one-to-many)
                    //we dont want to set FKs to the parent entity as the parent is cloned
                    if (parentEntity != null && parentEntity.ReferencedColumns.Contains(colNames.First()))
                        continue;
                    propertyInfo.SetValue(copiedEntity, propertyValue, null);
                }
            }
            return copiedEntity;
        }

        //can be an interface
        private static object CreateNewCollection(System.Type collectionType)
        {
            var concreteCollType = GetCollectionImplementation(collectionType);
            if (collectionType.IsGenericType)
            {
                concreteCollType = concreteCollType.MakeGenericType(collectionType.GetGenericArguments()[0]);
            }
            var propertyList = Activator.CreateInstance(concreteCollType);
            return propertyList;
        }

        private static void AddItemToCollection(object collection, object item, Func<object, object> editBeforeAdding = null)
        {
            var addMethod = collection.GetType().GetInterfaces()
                        .SelectMany(o => o.GetMethods())
                        .First(o => o.Name == "Add");

            var itemColl = item as IEnumerable;
            if (itemColl != null)
            {
                foreach (var colItem in itemColl)
                {
                    addMethod.Invoke(collection,
                                     editBeforeAdding != null
                                     ? new[] { editBeforeAdding(colItem) }
                                     : new[] { colItem });
                }
            }
            else
            {
                addMethod.Invoke(collection,
                                     editBeforeAdding != null
                                     ? new[] { editBeforeAdding(item) }
                                     : new[] { item });
            }
        }

        private static System.Type GetCollectionImplementation(System.Type collectionType)
        {
            if (collectionType.IsAssignableToGenericType(typeof(ISet<>)))
                return typeof(HashSet<>);
            if (collectionType.IsAssignableToGenericType(typeof(IList<>)))
                return typeof(List<>);
            if (collectionType.IsAssignableToGenericType(typeof(ICollection<>)))
                return typeof(List<>);
            if (collectionType.IsAssignableToGenericType(typeof(IEnumerable<>)))
                return typeof(List<>);
            throw new NotSupportedException(collectionType.FullName);
        }
    }
}
