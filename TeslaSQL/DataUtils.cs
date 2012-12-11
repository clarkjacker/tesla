﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Data;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Common;

namespace TeslaSQL {
    public class DataUtils : IDataUtils {

        public Logger logger;
        public Config config;

        public DataUtils(Config config, Logger logger) {
            this.config = config;
            this.logger = logger;
        }

        /// <summary>
        /// Runs a sql query and returns results as requested type
        /// </summary>
        /// <param name="server">Server to run the query on</param>
        /// <param name="dbName">Database name</param>
        /// <param name="cmd">SqlCommand to run</param>
        /// <param name="timeout">Query timeout</param>
        /// <returns>DataTable object representing the result</returns>
        private DataTable SqlQuery(TServer server, string dbName, SqlCommand cmd, int timeout = 30) {
            //build connection string based on server/db info passed in
            string connStr = buildConnString(server, dbName);

            //using block to avoid resource leaks
            using (SqlConnection conn = new SqlConnection(connStr)) {
                //open database connection
                conn.Open();
                cmd.Connection = conn;
                cmd.CommandTimeout = timeout;

                DataSet ds = new DataSet();
                SqlDataAdapter da = new SqlDataAdapter(cmd);
                //this is where the query is run
                da.Fill(ds);

                //return the result, which is the first DataTable in the DataSet
                return ds.Tables[0];
            }
        }

        /// <summary>
        /// Runs a sql query and returns first column and row from results as specified type
        /// </summary>
        /// <param name="server">Server to run the query on</param>
        /// <param name="dbName">Database name</param>
        /// <param name="cmd">SqlCommand to run</param>
        /// <param name="timeout">Query timeout</param>
        /// <returns>The value in the first column and row, as the specified type</returns>
        private T SqlQueryToScalar<T>(TServer server, string dbName, SqlCommand cmd, int timeout = 30) {
            DataTable result = SqlQuery(server, dbName, cmd, timeout);
            //return result in first column and first row as specified type
            return (T)result.Rows[0][0];
        }

        /// <summary>
        /// Runs a query that does not return results (i.e. a write operation)
        /// </summary>
        /// <param name="server">Server to run on</param>
        /// <param name="dbName">Database name</param>
        /// <param name="cmd">SqlCommand to run</param>
        /// <param name="timeout">Timeout (higher than selects since some writes can be large)</param>
        /// <returns>The number of rows affected</returns>
        private int SqlNonQuery(TServer server, string dbName, SqlCommand cmd, int timeout = 600) {
            //build connection string based on server/db info passed in
            string connStr = buildConnString(server, dbName);
            int numrows;
            //using block to avoid resource leaks
            using (SqlConnection conn = new SqlConnection(connStr)) {
                try {
                    //open database connection
                    conn.Open();
                    cmd.Connection = conn;
                    cmd.CommandTimeout = timeout;
                    numrows = cmd.ExecuteNonQuery();
                } catch (Exception e) {
                    //TODO figure out what to catch/rethrow
                    throw e;
                }
            }
            return numrows;
        }


        /// <summary>
        /// Builds a connection string for the passed in server identifier using global config values
        /// </summary>
        /// <param name="server">Server identifier</param>
        /// <param name="database">Database name</param>
        /// <returns>An ADO.NET connection string</returns>
        private string buildConnString(TServer server, string database) {
            string sqlhost = "";
            string sqluser = "";
            string sqlpass = "";

            switch (server) {
                case TServer.MASTER:
                    sqlhost = config.master;
                    sqluser = config.masterUser;
                    sqlpass = (new cTripleDes().Decrypt(config.masterPassword));
                    break;
                case TServer.SLAVE:
                    sqlhost = config.slave;
                    sqluser = config.slaveUser;
                    sqlpass = (new cTripleDes().Decrypt(config.slavePassword));
                    break;
                case TServer.RELAY:
                    sqlhost = config.relayServer;
                    sqluser = config.relayUser;
                    sqlpass = (new cTripleDes().Decrypt(config.relayPassword));
                    break;
            }

            return "Data Source=" + sqlhost + "; Initial Catalog=" + database + ";User ID=" + sqluser + ";Password=" + sqlpass;
        }


        public DataRow GetLastCTBatch(TServer server, string dbName, AgentType agentType, string slaveIdentifier = "") {
            SqlCommand cmd;
            //for slave we have to pass the slave identifier in and use tblCTSlaveVersion
            if (agentType.Equals(AgentType.Slave)) {
                cmd = new SqlCommand("SELECT TOP 1 CTID, syncStartVersion, syncStopVersion, syncBitWise" +
                    " FROM dbo.tblCTSlaveVersion WITH(NOLOCK) WHERE slaveIdentifier = @slave ORDER BY CTID DESC");
                cmd.Parameters.Add("@slave", SqlDbType.VarChar, 100).Value = slaveIdentifier;
            } else {
                cmd = new SqlCommand("SELECT TOP 1 CTID, syncStartVersion, syncStopVersion, syncBitWise FROM dbo.tblCTVersion ORDER BY CTID DESC");
            }

            DataTable result = SqlQuery(server, dbName, cmd);
            return result.Rows[0];
        }


        public DataTable GetPendingCTVersions(TServer server, string dbName, Int64 CTID, int syncBitWise) {
            string query = ("SELECT CTID, syncStartVersion, syncStopVersion, syncStartTime, syncBitWise" +
                " FROM dbo.tblCTVersion WITH(NOLOCK) WHERE CTID > @ctid AND syncBitWise & @syncbitwise > 0" +
                " ORDER BY CTID ASC");
            SqlCommand cmd = new SqlCommand(query);
            cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = syncBitWise;

            //get query results as a datatable since there can be multiple rows
            return SqlQuery(server, dbName, cmd);
        }


        public DateTime GetLastStartTime(TServer server, string dbName, Int64 CTID, int syncBitWise) {
            SqlCommand cmd = new SqlCommand("select MAX(syncStartTime) as maxStart FROM dbo.tblCTVersion WITH(NOLOCK)"
                + " WHERE syncBitWise & @syncbitwise > 0 AND CTID < @CTID");
            cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = syncBitWise;
            cmd.Parameters.Add("@CTID", SqlDbType.BigInt).Value = CTID;
            DateTime? lastStartTime = SqlQueryToScalar<DateTime?>(server, dbName, cmd);
            if (lastStartTime == null) {
                return DateTime.Now.AddDays(-1);
            }
            return (DateTime)lastStartTime;
        }


        public Int64 GetCurrentCTVersion(TServer server, string dbName) {
            SqlCommand cmd = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION();");
            return SqlQueryToScalar<Int64>(server, dbName, cmd);
        }


        public Int64 GetMinValidVersion(TServer server, string dbName, string table, string schema) {
            SqlCommand cmd = new SqlCommand("SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@tablename))");
            cmd.Parameters.Add("@tablename", SqlDbType.VarChar, 500).Value = schema + "." + table;
            return SqlQueryToScalar<Int64>(server, dbName, cmd);
        }


        public int SelectIntoCTTable(TServer server, string sourceCTDB, string masterColumnList, string ctTableName,
            string sourceDB, string schemaName, string tableName, Int64 startVersion, string pkList, Int64 stopVersion, string notNullPkList, int timeout) {
            /*
             * There is no way to have column lists or table names be parametrized/dynamic in sqlcommands other than building the string
             * manually like this. However, the table name and column list fields are trustworthy because they have already been compared to
             * actual database objects at this point. The database names are also validated to be legal database identifiers.
             * Only the start and stop versions are actually parametrizable.
             */
            string query = "SELECT " + masterColumnList + ", CT.SYS_CHANGE_VERSION, CT.SYS_CHANGE_OPERATION ";
            query += " INTO " + schemaName + "." + ctTableName;
            query += " FROM CHANGETABLE(CHANGES " + sourceDB + "." + schemaName + "." + tableName + ", @startVersion) CT";
            query += " LEFT OUTER JOIN " + sourceDB + "." + schemaName + "." + tableName + " P ON " + pkList;
            query += " WHERE (SYS_CHANGE_VERSION <= @stopVersion OR SYS_CHANGE_CREATION_VERSION <= @stopversion)";
            query += " AND (SYS_CHANGE_OPERATION = 'D' OR " + notNullPkList + ")";

            SqlCommand cmd = new SqlCommand(query);

            cmd.Parameters.Add("@startVersion", SqlDbType.BigInt).Value = startVersion;
            cmd.Parameters.Add("@stopVersion", SqlDbType.BigInt).Value = stopVersion;

            return SqlNonQuery(server, sourceCTDB, cmd, 1200);
        }


        public Int64 CreateCTVersion(TServer server, string dbName, Int64 syncStartVersion, Int64 syncStopVersion) {
            //create new row in tblCTVersion, output the CTID
            string query = "INSERT INTO dbo.tblCTVersion (syncStartVersion, syncStopVersion, syncStartTime, syncBitWise)";
            query += " OUTPUT inserted.CTID";
            query += " VALUES (@startVersion, @stopVersion, GETDATE(), 0)";

            SqlCommand cmd = new SqlCommand(query);

            cmd.Parameters.Add("@startVersion", SqlDbType.BigInt).Value = syncStartVersion;
            cmd.Parameters.Add("@stopVersion", SqlDbType.BigInt).Value = syncStopVersion;

            return SqlQueryToScalar<Int64>(server, dbName, cmd);
        }


        public void CreateSlaveCTVersion(TServer server, string dbName, Int64 CTID, string slaveIdentifier,
            Int64 syncStartVersion, Int64 syncStopVersion, DateTime syncStartTime, Int32 syncBitWise) {

            string query = "INSERT INTO dbo.tblCTSlaveVersion (CTID, slaveIdentifier, syncStartVersion, syncStopVersion, syncStartTime, syncBitWise)";
            query += " VALUES (@ctid, @slaveidentifier, @startversion, @stopversion, @starttime, @syncbitwise)";

            SqlCommand cmd = new SqlCommand(query);

            cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            cmd.Parameters.Add("@slaveidentifier", SqlDbType.VarChar, 100).Value = slaveIdentifier;
            cmd.Parameters.Add("@startversion", SqlDbType.BigInt).Value = syncStartVersion;
            cmd.Parameters.Add("@stopversion", SqlDbType.BigInt).Value = syncStopVersion;
            cmd.Parameters.Add("@starttime", SqlDbType.DateTime).Value = syncStartTime;
            cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = syncBitWise;

            int result = SqlNonQuery(server, dbName, cmd, 30);
        }


        public void CreateSchemaChangeTable(TServer server, string dbName, Int64 CTID) {
            //drop the table on the relay server if it exists
            bool tExisted = DropTableIfExists(server, dbName, "tblCTSchemaChange_" + Convert.ToString(CTID), "dbo");

            //can't parametrize the CTID since it's part of a table name, but it is an Int64 so it's not an injection risk
            string query = "CREATE TABLE [dbo].[tblCTSchemaChange_" + CTID + "] (";
            query += @"
            [CscID] [int] NOT NULL IDENTITY(1,1) PRIMARY KEY,
	        [CscDdeID] [int] NOT NULL,
	        [CscTableName] [varchar](500) NOT NULL,
            [CscEventType] [varchar](50) NOT NULL,
            [CscSchema] [varchar](100) NOT NULL,
            [CscColumnName] [varchar](500) NOT NULL,
            [CscNewColumnName] [varchar](500) NULL,
            [CscBaseDataType] [varchar](100) NULL,
            [CscCharacterMaximumLength] [int] NULL,
            [CscNumericPrecision] [int] NULL,
            [CscNumericScale] [int] NULL
            )";

            SqlCommand cmd = new SqlCommand(query);

            int result = SqlNonQuery(server, dbName, cmd);
        }


        public DataTable GetDDLEvents(TServer server, string dbName, DateTime afterDate) {
            if (!CheckTableExists(server, dbName, "tblDDLEvent")) {
                throw new Exception("tblDDLEvent does not exist on the source database, unable to check for schema changes. Please create the table and the trigger that populates it!");
            }

            string query = "SELECT DdeID, DdeEventData FROM dbo.tblDDLEvent WHERE DdeTime > @afterdate";

            SqlCommand cmd = new SqlCommand(query);
            cmd.Parameters.Add("@afterdate", SqlDbType.DateTime).Value = afterDate;

            return SqlQuery(server, dbName, cmd);
        }


        public void WriteSchemaChange(TServer server, string dbName, Int64 CTID, SchemaChange schemaChange) {

            string query = "INSERT INTO dbo.tblCTSchemaChange_" + Convert.ToString(CTID) +
                " (CscDdeID, CscTableName, CscEventType, CscSchema, CscColumnName, CscNewColumnName, " +
                " CscBaseDataType, CscCharacterMaximumLength, CscNumericPrecision, CscNumericScale) " +
                " VALUES (@ddeid, @tablename, @eventtype, @schema, @columnname, @newcolumnname, " +
                " @basedatatype, @charactermaximumlength, @numericprecision, @numericscale)";

            var cmd = new SqlCommand(query);
            cmd.Parameters.Add("@ddeid", SqlDbType.Int).Value = schemaChange.ddeID;
            cmd.Parameters.Add("@tablename", SqlDbType.VarChar, 500).Value = schemaChange.tableName;
            cmd.Parameters.Add("@eventtype", SqlDbType.VarChar, 50).Value = schemaChange.eventType;
            cmd.Parameters.Add("@schema", SqlDbType.VarChar, 100).Value = schemaChange.schemaName;
            cmd.Parameters.Add("@columnname", SqlDbType.VarChar, 500).Value = schemaChange.columnName;
            cmd.Parameters.Add("@newcolumnname", SqlDbType.VarChar, 500).Value = schemaChange.newColumnName;
            cmd.Parameters.Add("@basedatatype", SqlDbType.VarChar, 100).Value = schemaChange.dataType.baseType;
            cmd.Parameters.Add("@charactermaximumlength", SqlDbType.Int).Value = schemaChange.dataType.characterMaximumLength;
            cmd.Parameters.Add("@numericprecision", SqlDbType.Int).Value = schemaChange.dataType.numericPrecision;
            cmd.Parameters.Add("@numericscale", SqlDbType.Int).Value = schemaChange.dataType.numericScale;
            foreach (IDataParameter p in cmd.Parameters) {
                if (p.Value == null)
                    p.Value = DBNull.Value;
            }
            int result = SqlNonQuery(server, dbName, cmd);
        }


        public DataRow GetDataType(TServer server, string dbName, string table, string schema, string column) {
            var cmd = new SqlCommand("SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE " +
                "FROM INFORMATION_SCHEMA.COLUMNS WITH(NOLOCK) WHERE TABLE_SCHEMA = @schema AND TABLE_CATALOG = @db " +
                "AND TABLE_NAME = @table AND COLUMN_NAME = @column");
            cmd.Parameters.Add("@db", SqlDbType.VarChar, 500).Value = dbName;
            cmd.Parameters.Add("@table", SqlDbType.VarChar, 500).Value = table;
            cmd.Parameters.Add("@schema", SqlDbType.VarChar, 500).Value = schema;
            cmd.Parameters.Add("@column", SqlDbType.VarChar, 500).Value = column;

            DataTable result = SqlQuery(server, dbName, cmd);

            if (result == null || result.Rows.Count == 0) {
                throw new DoesNotExistException("Column " + column + " does not exist on table " + table + "!");
            }

            return result.Rows[0];
        }


        public void UpdateSyncStopVersion(TServer server, string dbName, Int64 syncStopVersion, Int64 CTID) {
            string query = "UPDATE dbo.tblCTVersion set syncStopVersion = @stopversion WHERE CTID = @ctid";
            SqlCommand cmd = new SqlCommand(query);

            cmd.Parameters.Add("@stopVersion", SqlDbType.BigInt).Value = syncStopVersion;
            cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;

            int res = SqlNonQuery(server, dbName, cmd);
        }


        /// <summary>
        /// Retrieves an SMO table object if the table exists, throws exception if not.
        /// </summary>
        /// <param name="server">Server idenitfier</param>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table Name</param>
        /// <returns>Smo.Table object representing the table</returns>
        private Table GetSmoTable(TServer server, string dbName, string table, string schema = "dbo") {
            using (SqlConnection sqlconn = new SqlConnection(buildConnString(server, dbName))) {
                ServerConnection serverconn = new ServerConnection(sqlconn);
                Server svr = new Server(serverconn);
                Database db = new Database();
                if (svr.Databases.Contains(dbName) && svr.Databases[dbName].IsAccessible) {
                    db = svr.Databases[dbName];
                } else {
                    throw new Exception("Database " + dbName + " does not exist or is inaccessible");
                }
                if (db.Tables.Contains(table)) {
                    return db.Tables[table, schema];
                } else {
                    throw new DoesNotExistException("Table " + table + " does not exist");
                }
            }
        }


        public bool CheckTableExists(TServer server, string dbName, string table, string schema = "dbo") {
            try {
                Table t_smo = GetSmoTable(server, dbName, table, schema);

                if (t_smo != null) {
                    return true;
                }
                return false;
            } catch (DoesNotExistException) {
                return false;
            }
        }


        public string GetIntersectColumnList(TServer server, string dbName, string table1, string schema1, string table2, string schema2) {
            Table t_smo_1 = GetSmoTable(server, dbName, table1, schema1);
            Table t_smo_2 = GetSmoTable(server, dbName, table2, schema2);
            string columnList = "";

            //list to hold lowercased column names
            var columns_2 = new List<string>();

            //create this so that casing changes to columns don't cause problems, just use the lowercase column name
            foreach (Column c in t_smo_2.Columns) {
                columns_2.Add(c.Name.ToLower());
            }

            foreach (Column c in t_smo_1.Columns) {
                //case insensitive comparison using ToLower()
                if (columns_2.Contains(c.Name.ToLower())) {
                    if (columnList != "") {
                        columnList += ",";
                    }

                    columnList += "[" + c.Name + "]";
                }
            }
            return columnList;
        }


        public bool HasPrimaryKey(TServer server, string dbName, string tableName, string schema) {
            Table table = GetSmoTable(server, dbName, tableName, schema);
            foreach (Index i in table.Indexes) {
                if (i.IndexKeyType == IndexKeyType.DriPrimaryKey) {
                    return true;
                }
            }
            return false;
        }


        public bool DropTableIfExists(TServer server, string dbName, string table, string schema) {
            try {
                Table t_smo = GetSmoTable(server, dbName, table, schema);
                if (t_smo != null) {
                    t_smo.Drop();
                    return true;
                }
                return false;
            } catch (DoesNotExistException) {
                return false;
            }
        }

        /// <summary>
        /// Runs a query on the source server and copies the resulting data to the destination
        /// </summary>
        /// <param name="sourceServer">Source server identifier</param>
        /// <param name="sourceDB">Source database name</param>
        /// <param name="destServer">Destination server identifier</param>
        /// <param name="destDB">Destination database name</param>
        /// <param name="cmd">Query to get the data from</param>
        /// <param name="destinationTable">Table to write to on the destination (must already exist)</param>
        /// <param name="queryTimeout">How long the query on the source can run for</param>
        /// <param name="bulkCopyTimeout">How long writing to the destination can take</param>
        private void CopyDataFromQuery(TServer sourceServer, string sourceDB, TServer destServer, string destDB, SqlCommand cmd, string destinationTable, string destinationSchema = "dbo", int queryTimeout = 36000, int bulkCopyTimeout = 36000) {
            using (SqlConnection sourceConn = new SqlConnection(buildConnString(sourceServer, sourceDB))) {
                sourceConn.Open();
                cmd.Connection = sourceConn;
                cmd.CommandTimeout = queryTimeout;
                SqlDataReader reader = cmd.ExecuteReader();

                SqlBulkCopy bulkCopy = new SqlBulkCopy(buildConnString(destServer, destDB), SqlBulkCopyOptions.KeepIdentity);
                bulkCopy.BulkCopyTimeout = bulkCopyTimeout;
                bulkCopy.DestinationTableName = destinationSchema + "." + destinationTable;
                bulkCopy.WriteToServer(reader);
            }
        }

        public void CopyTable(TServer sourceServer, string sourceDB, string table, string schema, TServer destServer, string destDB, int timeout) {
            //drop table at destination and create from source schema
            CopyTableDefinition(sourceServer, sourceDB, table, schema, destServer, destDB);

            //can't parametrize tablename or schema name but they have already been validated against the server so it's safe
            SqlCommand cmd = new SqlCommand("SELECT * FROM " + schema + "." + table);
            CopyDataFromQuery(sourceServer, sourceDB, destServer, destDB, cmd, table, schema, timeout, timeout);
        }


        /// <summary>
        /// Copies the schema of a table from one server to another, dropping it first if it exists at the destination.
        /// </summary>
        /// <param name="sourceServer">Source server identifier</param>
        /// <param name="sourceDB">Source database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <param name="destServer">Destination server identifier</param>
        /// <param name="destDB">Destination database name</param>
        private void CopyTableDefinition(TServer sourceServer, string sourceDB, string table, string schema, TServer destServer, string destDB) {
            //script out the table at the source
            string createScript = ScriptTable(sourceServer, sourceDB, table, schema);
            SqlCommand cmd = new SqlCommand(createScript);

            //drop it if it exists at the destination
            bool didExist = DropTableIfExists(destServer, destDB, table, schema);

            //create it at the destination
            int result = SqlNonQuery(destServer, destDB, cmd);
        }


        /// <summary>
        /// Scripts out a table as CREATE TABLE
        /// </summary>
        /// <param name="server">Server identifier to connect to</param>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>The CREATE TABLE script as a string</returns>
        private string ScriptTable(TServer server, string dbName, string table, string schema) {
            //initialize scriptoptions variable
            ScriptingOptions scriptOptions = new ScriptingOptions();
            scriptOptions.ScriptBatchTerminator = true;
            scriptOptions.NoCollation = true;

            //get smo table object
            Table t_smo = GetSmoTable(server, dbName, table, schema);

            //script out the table, it comes back as a StringCollection object with one string per query batch
            StringCollection scriptResults = t_smo.Script(scriptOptions);

            //ADO.NET does not allow multiple batches in one query, but we don't really need the
            //SET ANSI_NULLS ON etc. statements, so just find the CREATE TABLE statement and return that
            foreach (string s in scriptResults) {
                if (s.StartsWith("CREATE")) {
                    return s;
                }
            }
            return "";
        }


        public Dictionary<string, bool> GetFieldList(TServer server, string dbName, string table, string schema) {
            Dictionary<string, bool> dict = new Dictionary<string, bool>();
            Table t_smo;

            //attempt to get smo table object
            try {
                t_smo = GetSmoTable(server, dbName, table, schema);
            } catch (DoesNotExistException) {
                //TODO figure out if we also want to throw here
                logger.Log("Unable to get field list for table " + table + " because it does not exist", LogLevel.Error);
                return dict;
            }

            //loop through columns and add them to the dictionary along with whether they are part of the primary key
            foreach (Column c in t_smo.Columns) {
                dict.Add(c.Name, c.InPrimaryKey);
            }

            return dict;
        }


        public void WriteBitWise(TServer server, string dbName, Int64 CTID, int value, AgentType agentType) {
            string query;
            SqlCommand cmd;
            if (agentType.Equals(AgentType.Slave)) {
                query = "UPDATE dbo.tblCTSlaveVersion SET SyncBitWise = SyncBitWise + @syncbitwise";
                query += " WHERE slaveIdentifier = @slaveidentifier AND CTID = @ctid AND SyncBitWise & @syncbitwise = 0";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = value;
                cmd.Parameters.Add("@slaveidentifier", SqlDbType.VarChar, 100).Value = config.slave;
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            } else {
                query = "UPDATE dbo.tblCTVersion SET SyncBitWise = SyncBitWise + @syncbitwise";
                query += " WHERE CTID = @ctid AND SyncBitWise & @syncbitwise = 0";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = value;
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            }
            int result = SqlNonQuery(server, dbName, cmd);
        }


        public int ReadBitWise(TServer server, string dbName, Int64 CTID, AgentType agentType) {
            string query;
            SqlCommand cmd;
            if (agentType.Equals(AgentType.Slave)) {
                query = "SELECT syncBitWise from dbo.tblCTSlaveVersion WITH(NOLOCK)";
                query += " WHERE slaveIdentifier = @slaveidentifier AND CTID = @ctid";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@slaveidentifier", SqlDbType.VarChar, 100).Value = config.slave;
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            } else {
                query = "SELECT syncBitWise from dbo.tblCTVersion WITH(NOLOCK)";
                query += " WHERE CTID = @ctid";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            }
            return SqlQueryToScalar<Int32>(server, dbName, cmd);
        }


        public void MarkBatchComplete(TServer server, string dbName, Int64 CTID, Int32 syncBitWise, DateTime syncStopTime, AgentType agentType, string slaveIdentifier = "") {
            string query;
            SqlCommand cmd;
            if (agentType.Equals(AgentType.Slave)) {
                query = "UPDATE dbo.tblCTSlaveVersion SET syncBitWise += @syncbitwise, syncStopTime = @syncstoptime";
                query += " WHERE slaveIdentifier = @slaveidentifier AND CTID = @ctid AND SyncBitWise & @syncbitwise = 0";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = syncBitWise;
                cmd.Parameters.Add("@syncstoptime", SqlDbType.DateTime).Value = syncStopTime;
                cmd.Parameters.Add("@slaveidentifier", SqlDbType.VarChar, 100).Value = slaveIdentifier;
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            } else {
                query = "UPDATE dbo.tblCTVersion SET SyncBitWise += @syncbitwise";
                query += " WHERE CTID = @ctid AND SyncBitWise & @syncbitwise = 0";
                cmd = new SqlCommand(query);
                cmd.Parameters.Add("@syncbitwise", SqlDbType.Int).Value = syncBitWise;
                cmd.Parameters.Add("@syncstoptime", SqlDbType.DateTime).Value = syncStopTime;
                cmd.Parameters.Add("@ctid", SqlDbType.BigInt).Value = CTID;
            }
            int result = SqlNonQuery(server, dbName, cmd);
        }


        public DataTable GetSchemaChanges(TServer server, string dbName, Int64 CTID) {
            SqlCommand cmd = new SqlCommand("SELECT CscID, CscDdeID, CscTableName, CscEventType, CscSchema, CscColumnName" +
             ", CscNewColumnName, CscBaseDataType, CscCharacterMaximumLength, CscNumericPrecision, CscNumericScale" +
             " FROM dbo.tblCTSchemaChange_" + Convert.ToString(CTID));
            return SqlQuery(server, dbName, cmd);
        }


        public Int64 GetTableRowCount(TServer server, string dbName, string table, string schema) {
            Table t_smo = GetSmoTable(server, dbName, table, schema);
            return t_smo.RowCount;
        }

        public bool IsChangeTrackingEnabled(TServer server, string dbName, string table, string schema) {
            Table t_smo = GetSmoTable(server, dbName, table, schema);
            return t_smo.ChangeTrackingEnabled;
        }

        public void RenameColumn(TableConf t, TServer server, string dbName, string schema, string table,
            string columnName, string newColumnName) {
            var cmd = new SqlCommand("EXEC sp_rename @objname, @newname, 'COLUMN'");
            cmd.Parameters.Add("@objname", SqlDbType.VarChar, 500).Value = schema + "." + table + "." + columnName;
            cmd.Parameters.Add("@newname", SqlDbType.VarChar, 500).Value = newColumnName;

            int result = SqlNonQuery(server, dbName, cmd);
            //check for history table, if it is configured we need to modify that too
            if (t.recordHistoryTable) {
                cmd = new SqlCommand("EXEC sp_rename @objname, @newname, 'COLUMN'");
                //TODO verify the _History suffix is correct
                cmd.Parameters.Add("@objname", SqlDbType.VarChar, 500).Value = schema + "." + table + "_History." + columnName;
                cmd.Parameters.Add("@newname", SqlDbType.VarChar, 500).Value = newColumnName;
                result = SqlNonQuery(server, dbName, cmd);
            }

        }

        public void LogError(string message) {
            SqlCommand cmd = new SqlCommand("INSERT INTO tblCtError (CelError) VALUES ( @error )");
            cmd.Parameters.Add("@error", SqlDbType.VarChar, 1000).Value = message;
            SqlNonQuery(TServer.RELAY, config.errorLogDB, cmd);
        }

        public DataTable GetUnsentErrors() {
            SqlCommand cmd = new SqlCommand("SELECT CelError, CelId FROM tblCtError WHERE CelSent = 0");
            return SqlQuery(TServer.RELAY, config.errorLogDB, cmd);
        }

        public void MarkErrorsSent(IEnumerable<int> celIds) {
            SqlCommand cmd = new SqlCommand("UPDATE tblCtError SET CelSent = 1 WHERE CelId IN (" + string.Join(",", celIds) + ")");
            SqlNonQuery(TServer.RELAY, config.errorLogDB, cmd);
        }


        public void PublishTableInfo(TableConf t, Int64 CTID) {

        }


        public void CreateTableInfoTable(TServer server, string dbName, Int64 CTID) {
            //drop the table on the relay server if it exists
            bool tExisted = DropTableIfExists(server, dbName, "tblCTTableInfo_" + CTID, "dbo");

            //can't parametrize the CTID since it's part of a table name, but it is an Int64 so it's not an injection risk
            string query = "CREATE TABLE [dbo].[tblCTTableInfo_" + CTID + "] (";
            query += @"
            [CtiID] [int] NOT NULL IDENTITY(1,1) PRIMARY KEY,
	        [CtiTableName] [varchar](500) NOT NULL,
            [CtiSchemaName] [varchar](100) NOT NULL,
            [CtiPKList] [varchar](500) NOT NULL,
            [CtiNotNullPKList] [varchar](500) NOT NULL,
            [CtiExpectedRows] [int] NOT NULL,
            )";

            SqlCommand cmd = new SqlCommand(query);

            int result = SqlNonQuery(server, dbName, cmd);
        }


        public void PublishTableInfo(TServer server, string dbName, TableConf t, long CTID, long expectedRows) {
            SqlCommand cmd = new SqlCommand(
               String.Format(@"INSERT INTO tblCTTableInfo_{0} (CtiTableName, CtiSchemaName, CtiPKList, CtiNotNullPKList, CtiExpectedRows)
                  VALUES (@tableName, @schemaName, @pkList, @notNullPKList, @expectedRows)", CTID));
            cmd.Parameters.Add("@tableName", SqlDbType.VarChar, 500).Value = t.Name;
            cmd.Parameters.Add("@schemaName", SqlDbType.VarChar, 500).Value = t.schemaName;
            cmd.Parameters.Add("@pkList", SqlDbType.VarChar, 500).Value = t.pkList;
            cmd.Parameters.Add("@notNullPKList", SqlDbType.VarChar, 500).Value = t.notNullPKList;
            cmd.Parameters.Add("@expectedRows", SqlDbType.Int).Value = expectedRows;

            SqlNonQuery(server, dbName, cmd);
        }
    }
}
