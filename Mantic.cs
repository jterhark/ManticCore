﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ManticFramework
{
    public class Mantic
    {
        private Dictionary<string, Dictionary<string, Mapping>> _columnMappings;
        private Dictionary<string, string> _tableMappings;
        private Dictionary<string, string> _selectQueries;
        private Dictionary<string, string> _insertQueries;

        private string ConnectionString { get; set; }
        

        public Mantic(string connectionString = null)
        {
            _columnMappings = new Dictionary<string, Dictionary<string, Mapping>>();
            _tableMappings = new Dictionary<string, string>();
            _selectQueries = new Dictionary<string, string>();
            _insertQueries = new Dictionary<string, string>();
            ConnectionString = connectionString;
        }

        private bool IsRegistered<T>(Type t) => _columnMappings.ContainsKey(t.FullName);

        public bool HasMappedTable<T>() => _tableMappings.ContainsKey(typeof(T).FullName);


        public void Register<T>()
        {
            var type = typeof(T);
            var props = type.GetProperties();
            var map = new Dictionary<string, Mapping>();
            
            foreach (var prop in props)
            {
                var l = prop.GetCustomAttributes(typeof(ManticSqlColumn), true);

                if (l.Length != 1 || !(l[0] is ManticSqlColumn attrib))
                {
                    continue;
                }

                var propType = prop.PropertyType;

                //if adding support for non attributed props, add optional bool param addAll and skip nullable check
                if (propType.IsValueType && Nullable.GetUnderlyingType(propType) == null)
                {
                    throw new ArgumentException("Properties having the SqlColumnAttribute must be nullable!");
                }

                var propName = $"{type.FullName}.{prop.Name}";

                SqlDbType? s;
                attrib.TryGetDbType(out s);

                map.Add(prop.Name, new Mapping
                {
                    PropertyType = propType,
                    SqlColumnName = attrib.Name,
                    SqlColumnType = s ?? null,
                    SqlColumnLength = attrib.ColumnLength,
                    IgnoreOnInsert = attrib.IgnoreOnInsert
                });
            }

            _columnMappings.Add(type.FullName, map);


            var x = type.GetCustomAttributes(typeof(ManticSqlTable), true);
            if (x.Length > 1)
            {
                throw new ArgumentException("A single class cannot contain multiple SqlTable attributes! Check inheritance.");
            }
            else if (x.Length == 1 && (x[0] is ManticSqlTable t))
            {
                _tableMappings.Add(type.FullName, t.Table);
                _selectQueries.Add(type.FullName, $"SELECT * FROM {_tableMappings[typeof(T).FullName]}");


                var builder = new StringBuilder();
                builder.Append("INSERT INTO ")
                    .Append(_tableMappings[type.FullName])
                    .Append('(')
                    .Append(string.Join(',', _columnMappings[type.FullName]
                        .Where(i => !(i.Value.IgnoreOnInsert.HasValue && i.Value.IgnoreOnInsert.Value))
                        .Select(i => $"[{i.Value.SqlColumnName}]")))
                    .Append(") VALUES(")
                    .Append(string.Join(',', _columnMappings[type.FullName]
                        .Where(i => !(i.Value.IgnoreOnInsert.HasValue && i.Value.IgnoreOnInsert.Value))
                        .Select(i => $"@{i.Value.SqlColumnName}")))
                    .Append(')');
                _insertQueries.Add(type.FullName, builder.ToString());
            }

        }

        public IEnumerable<T> Fill<T>(DataTable dt) where T : new()
        {
            var type = typeof(T);

            if (!IsRegistered<T>(type))
            {
                throw new ArgumentException("Class not registered!");
            }

            foreach (DataRow row in dt.Rows)
            {
                var obj = new T();
                var props = obj.GetType().GetProperties();
                foreach (var x in _columnMappings[type.FullName])
                {
                    if (dt.Columns.Contains(x.Value.SqlColumnName))
                    {
                        type.GetProperty(x.Key).SetValue(obj, row[x.Value.SqlColumnName] != DBNull.Value ? row[x.Value.SqlColumnName] : null);
                    }
                    else
                    {
                        type.GetProperty(x.Key).SetValue(obj, null);
                    }
                }

                yield return obj;
            }

        }

        public async Task<IEnumerable<T>> Query<T>(string command, bool storedProcedure = false) where T : new()
        {
            var cmd = new SqlCommand(command);

            if (storedProcedure)
            {
                cmd.CommandType = CommandType.StoredProcedure;
            }

            return await Query<T>(cmd);
        }

        public async Task<IEnumerable<T>> Query<T>(SqlCommand command) where T : new()
        {
            if (ConnectionString == null)
            {
                throw new ArgumentNullException("ConnectionString", "Connection string not set!");
            }

            if (command == null)
            {
                throw new ArgumentNullException("command", "You need a non-null sql command to query a database.");
            }

            var dt = new DataTable();

            using (var conn = new SqlConnection(ConnectionString))
            {
                await conn.OpenAsync();
                command.Connection = conn;
                new SqlDataAdapter(command).Fill(dt);
            }

            return Fill<T>(dt);
        }

        public async Task<IEnumerable<T>> All<T>() where T : new()
        {
            if (!IsRegistered<T>(typeof(T)))
            {
                throw new ArgumentException("Class is not registered!");
            }

            if (!HasMappedTable<T>())
            {
                throw new ArgumentException("Class does not have a mapped table. Use Sql Table.");
            }

            return await Query<T>(_selectQueries[typeof(T).FullName]);
        }

        public async void Insert<T>(T data) where T : new()
        {
            var t = data.GetType();

            if (!HasMappedTable<T>())
            {
                throw new ArgumentException("Class has no mapped table. Use SqlTable.");
            }
            if (!IsRegistered<T>(t))
            {
                throw new ArgumentException("Class is not registered");
            }

            var cmd = new SqlCommand(_insertQueries[t.FullName]);

            foreach (var entry in _columnMappings[t.FullName])
            {
                var obj = new object();
                var val = t.GetProperty(entry.Key).GetValue(data, null);

                if (!entry.Value.SqlColumnType.HasValue)
                {
                    throw new ArgumentException("To use insert you must specify a sql column data type! :'(");
                }

                if (val == null)
                {
                    cmd.Parameters.Add($"@{entry.Value.SqlColumnName}", entry.Value.SqlColumnType.Value).Value = DBNull.Value;
                }
                else if (entry.Value.SqlColumnLength == null)
                {
                    cmd.Parameters.Add($"@{entry.Value.SqlColumnName}", entry.Value.SqlColumnType.Value).Value = val;
                }
                else
                {
                    cmd.Parameters.Add($"@{entry.Value.SqlColumnName}", entry.Value.SqlColumnType.Value, entry.Value.SqlColumnLength.Value).Value = val;
                }
            }

            using (var conn = new SqlConnection(this.ConnectionString))
            {
                cmd.Connection = conn;
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }
    }


    internal class Mapping
    {
        public Type PropertyType { get; set; }

        public string SqlColumnName { get; set; }

        public SqlDbType? SqlColumnType { get; set; }

        public int? SqlColumnLength { get; set; }

        public bool? IgnoreOnInsert { get; set; }
    }
}
