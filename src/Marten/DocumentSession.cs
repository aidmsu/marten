﻿using System;
using System.Collections.Generic;
using System.Linq;
using FubuCore;
using Marten.Linq;
using Marten.Schema;
using Marten.Util;
using Npgsql;
using Remotion.Linq;
using Remotion.Linq.Parsing.Structure;

namespace Marten
{
    public interface IMartenQueryExecutor : IQueryExecutor
    {
        NpgsqlCommand BuildCommand<T>(QueryModel queryModel);
    }

    public class DocumentSession : IDocumentSession
    {
        private readonly IList<NpgsqlCommand> _deletes = new List<NpgsqlCommand>();
        private readonly IQueryParser _parser;
        private readonly IMartenQueryExecutor _executor;
        private readonly CommandRunner _runner;
        private readonly IDocumentSchema _schema;
        private readonly ISerializer _serializer;

        private readonly IList<object> _updates = new List<object>();

        public DocumentSession(IDocumentSchema schema, ISerializer serializer, IConnectionFactory factory, IQueryParser parser, IMartenQueryExecutor executor)
        {
            _schema = schema;
            _serializer = serializer;
            _parser = parser;
            _executor = executor;
            _runner = new CommandRunner(factory);
        }


        public void Dispose()
        {
        }

        public void Delete<T>(T entity)
        {
            var storage = _schema.StorageFor(typeof (T));
            _deletes.Add(storage.DeleteCommandForEntity(entity));
        }

        public void Delete<T>(ValueType id)
        {
            var storage = _schema.StorageFor(typeof (T));
            _deletes.Add(storage.DeleteCommandForId(id));
        }

        public void Delete<T>(string id)
        {
            var storage = _schema.StorageFor(typeof (T));
            _deletes.Add(storage.DeleteCommandForId(id));
        }

        public T Load<T>(string id)
        {
            return load<T>(id);
        }

        public T Load<T>(ValueType id)
        {
            return load<T>(id);
        }


        public void SaveChanges()
        {
            // TODO -- fancier later to add batch updating!

            _runner.Execute(conn =>
            {
                using (var tx = conn.BeginTransaction())
                {
                    _updates.Each(o =>
                    {
                        var docType = o.GetType();
                        var storage = _schema.StorageFor(docType);

                        var json = _serializer.ToJson(o);
                        using (var command = storage.UpsertCommand(o, json))
                        {
                            command.Connection = conn;
                            command.Transaction = tx;

                            command.ExecuteNonQuery();
                        }
                    });

                    _deletes.Each(cmd =>
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.ExecuteNonQuery();
                    });

                    tx.Commit();

                    _deletes.Clear();
                    _updates.Clear();
                }
            });
        }

        public void Store(object entity)
        {
            // TODO -- throw if null
            _updates.Add(entity);
        }

        public IQueryable<T> Query<T>()
        {
            return new MartenQueryable<T>(_parser, _executor);
        }

        public IEnumerable<T> Query<T>(string @where, params object[] parameters)
        {
            var tableName = _schema.StorageFor(typeof (T)).TableName;
            var sql = "select data from {0} {1}".ToFormat(tableName, @where);
            var cmd = new NpgsqlCommand();

            parameters.Each(x =>
            {
                var param = cmd.AddParameter(x);
                sql = sql.UseParameter(param);
            });

            cmd.CommandText = sql;

            return _serializer.FromJson<T>(_runner.QueryJson(cmd));
        }

        public ILoadByKeys<T> Load<T>()
        {
            return new LoadByKeys<T>(this);
        }

        private T load<T>(object id)
        {
            var storage = _schema.StorageFor(typeof (T));
            var loader = storage.LoaderCommand(id);

            return _runner.Execute(conn =>
            {
                loader.Connection = conn;
                var json = loader.ExecuteScalar() as string; // Maybe do this as a stream later for big docs?

                if (json == null) return default(T);

                return _serializer.FromJson<T>(json);
            });
        }


        public class LoadByKeys<TDoc> : ILoadByKeys<TDoc>
        {
            private readonly DocumentSession _parent;

            public LoadByKeys(DocumentSession parent)
            {
                _parent = parent;
            }

            public IEnumerable<TDoc> ById<TKey>(params TKey[] keys)
            {
                var storage = _parent._schema.StorageFor(typeof (TDoc));
                var cmd = storage.LoadByArrayCommand(keys);


                return _parent._runner.Query<TDoc>(cmd, _parent._serializer);
            }

            public IEnumerable<TDoc> ById<TKey>(IEnumerable<TKey> keys)
            {
                return ById(keys.ToArray());
            }
        }
    }
}