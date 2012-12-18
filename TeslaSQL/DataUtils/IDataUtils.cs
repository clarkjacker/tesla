using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Data;

namespace TeslaSQL.DataUtils {
    public interface IDataUtils {
        /// <summary>
        /// Gets information on the last CT batch relevant to this agent
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="agentType">We need to query a different table for master vs. slave</param>
        /// <param name="slaveIdentifier">Hostname of the slave if applicable</param>
        DataRow GetLastCTBatch(string dbName, AgentType agentType, string slaveIdentifier = "");

        /// <summary>
        /// Gets CT versions that are greater than the passed in CTID and have the passed in bitwise value
        /// </summary>
        /// <param name="dbName">Database name to check</param>
        /// <param name="CTID">Pull CTIDs greater than this one</param>
        /// <param name="syncBitWise">Only include versions containing this bit</param>
        DataTable GetPendingCTVersions(string dbName, Int64 CTID, int syncBitWise);

        DataTable GetPendingCTSlaveVersions(string dbName);

        /// <summary>
        /// Gets the start time of the last successful CT batch before the specified CTID
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">Current CTID</param>
        /// <param name="syncBitWise">syncBitWise value to compare against</param>
        /// <returns>Datetime representing last succesful run</returns>
        DateTime GetLastStartTime(string dbName, Int64 CTID, int syncBitWise);

        /// <summary>
        /// Gets the CHANGE_TRACKING_CURRENT_VERSION() for a database
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <returns>Current change tracking version</returns>
        Int64 GetCurrentCTVersion(string dbName);

        /// <summary>
        /// Gets the minimum valid CT version for a table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schame</param>
        /// <returns>Minimum valid version</returns>
        Int64 GetMinValidVersion(string dbName, string table, string schema);

        /// <summary>
        /// Creates a new row in tblCTVersion
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="syncStartVersion">Version number the batch starts at</param>
        /// <param name="syncStopVersion">Version number the batch ends at</param>
        /// <returns>CTID generated by the database</returns>
        Int64 CreateCTVersion(string dbName, Int64 syncStartVersion, Int64 syncStopVersion);

        /// <summary>
        /// Generates and runs SELECT INTO query to create a changetable
        /// </summary>
        /// <param name="sourceCTDB">Source CT database name</param>
        /// <param name="schemaName">Source schema name</param>
        /// <param name="masterColumnList">column list for the select statement</param>
        /// <param name="ctTableName">CT table name</param>
        /// <param name="sourceDB">Source database name</param>
        /// <param name="tableName">Table name</param>
        /// <param name="startVersion">syncStartVersion for the batch</param>
        /// <param name="pkList">Primary key list for join condition</param>
        /// <param name="stopVersion">syncStopVersion for the batch</param>
        /// <param name="notNullPkList">Primary key list for where clause</param>
        /// <param name="timeout">How long this is allowed to run for (seconds)</param>
        /// <returns>Int representing the number of rows affected (number of changes captured)</returns>
        int SelectIntoCTTable(string sourceCTDB, string masterColumnList, string ctTableName,
            string sourceDB, string schemaName, string tableName, Int64 startVersion, string pkList, Int64 stopVersion, string notNullPkList, int timeout);

        /// <summary>
        /// Creates a row in tblCTSlaveVersion
        /// </summary>
        /// <param name="dbName">Database name to write to</param>
        /// <param name="slaveIdentifier">Slave identifier string (usually hostname)</param>
        /// <param name="CTID">Batch number (generated on master)</param>
        /// <param name="syncStartVersion">Version number the batch starts at</param>
        /// <param name="syncStopVersion">Version number the batch ends at</param>
        /// <param name="syncBitWise">Current bitwise value for the batch</param>
        /// <param name="syncStartTime">Time the batch started on the master</param>
        void CreateSlaveCTVersion(string dbName, ChangeTrackingBatch ctb, string slaveIdentifier);

        /// <summary>
        /// Create the tblCTSchemaChange_(version) table on the relay server, dropping if it already exists
        /// </summary>
        /// <param name="dbName">Database to run on</param>
        /// <param name="CTID">CT version number</param>
        void CreateSchemaChangeTable(string dbName, Int64 CTID);

        /// <summary>
        /// Get DDL events from tblDDLEvent that occurred after the specified date
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="afterDate">Date to start from</param>
        /// <returns>DataTable object representing the events</returns>
        DataTable GetDDLEvents(string dbName, DateTime afterDate);

        /// <summary>
        /// Writes a schema change record to the appropriate schema change table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">Batch ID</param>
        /// <param name="schemaChange">Schema change object to write to the database</param>
        void WriteSchemaChange(string dbName, Int64 CTID, SchemaChange schemaChange);

        /// <summary>
        /// Gets a column's data type
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">The table's schema</param>
        /// <param name="column">Column name to get the data type of</param>
        /// <returns>DataRow representing the data type</returns>
        DataRow GetDataType(string dbName, string table, string schema, string column);

        /// <summary>
        /// Updates the syncStopVersion in tblCTVersion to the specified value for the specified CTID
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="syncStopVersion">New syncStopVersion</param>
        /// <param name="CTID">Batch identifier</param>
        void UpdateSyncStopVersion(string dbName, Int64 syncStopVersion, Int64 CTID);

        /// <summary>
        /// Check to see if a table exists on the specified server
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name to check for</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>Boolean representing whether or not the table exists.</returns>
        bool CheckTableExists(string dbName, string table, string schema = "dbo");

        /// <summary>
        /// Compares two tables and retrieves a column list that is an intersection of the columns they contain
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table1">First table</param>
        /// <param name="schema1">First table's schema</param>
        /// <param name="table2">Second table (order doesn't matter)</param>
        /// <param name="schema2">Second table's schema</param>
        /// <returns>String containing the resulting intersect column list</returns>
        IEnumerable<string> GetIntersectColumnList(string dbName, string table1, string schema1, string table2, string schema2);

        /// <summary>
        /// Check whether a table has a primary key
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">First table</param>
        /// <param name="schema">Schema name</param>
        /// <returns>True if the table has a primary key, otherwise false</returns>
        bool HasPrimaryKey(string dbName, string table, string schema);

        /// <summary>
        /// Checks to see if a table exists on the specified server and drops it if so.
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>Boolean specifying whether or not the table existed</returns>
        bool DropTableIfExists(string dbName, string table, string schema);

        /// <summary>
        /// Gets a dictionary of columns for a table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>Dictionary with column name as key and a bool representing whether it's part of the primary key as value</returns>
        Dictionary<string, bool> GetFieldList(string dbName, string table, string schema);

        /// <summary>
        /// Adds the specified bit to the syncBitWise column in tblCTVersion/tblCTSlaveVersion
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">CT version number</param>
        /// <param name="value">Bit to add</param>
        /// <param name="agentType">Agent type running this request (if it's slave we use tblCTSlaveVersion)</param>
        void WriteBitWise(string dbName, Int64 CTID, int value, AgentType agentType);

        /// <summary>
        /// Gets syncbitwise for specified CT version table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">CT version number to check</param>
        /// <param name="agentType">Agent type running this request (if it's slave we use tblCTSlaveVersion)</param>
        int ReadBitWise(string dbName, Int64 CTID, AgentType agentType);

        /// <summary>
        /// Marks a CT batch as complete
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">CT batch ID</param>
        /// <param name="syncStopTime">Stop time to write</param>
        /// <param name="slaveIdentifier">For slave agents, the slave hostname or ip</param>
        void MarkBatchComplete(string dbName, Int64 CTID, DateTime syncStopTime, string slaveIdentifier);

        /// <summary>
        /// Pulls the list of schema changes for a CTID
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="CTID">change tracking batch ID</param>
        /// <returns>DataTable object containing the query results</returns>
        DataTable GetSchemaChanges(string dbName, Int64 CTID);

        /// <summary>
        /// Gets the rowcounts for a table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>The number of rows in the table</returns>
        Int64 GetTableRowCount(string dbName, string table, string schema);

        /// <summary>
        /// Checks whether change tracking is enabled on a table
        /// </summary>
        /// <param name="dbName">Database name</param>
        /// <param name="table">Table name</param>
        /// <param name="schema">Table's schema</param>
        /// <returns>True if it is enabled, false if it's not.</returns>
        bool IsChangeTrackingEnabled(string dbName, string table, string schema);

        void LogError(string message);

        DataTable GetUnsentErrors();

        void MarkErrorsSent(IEnumerable<int> celIds);

        /// <summary>
        /// Renames a column in a table, and the associated history table if recording history is configured
        /// <summary>
        /// <param name="t">TableConf object for the table</param>
        /// <param name="dbName">Database name the table lives in</param>
        /// <param name="schema">Schema the table is part of</param>
        /// <param name="table">Table name</param>
        /// <param name="columnName">Old column name</param>
        /// <param name="newColumnName">New column name</param>
        void RenameColumn(TableConf t, string dbName, string schema, string table, string columnName, string newColumnName);

        /// <summary>
        /// Changes a column's data type
        /// </summary>
        /// <param name="t">TableConf object for the table</param>
        /// <param name="dbName">Database name the table lives in</param>
        /// <param name="schema">Schema the table is part of</param>
        /// <param name="table">Table name</param>
        /// <param name="columnName">Column name to modify</param>
        /// <param name="dataType">String representation of the column's new data type</param>
        void ModifyColumn(TableConf t, string dbName, string schema, string table, string columnName, string dataType);

        /// <summary>
        /// Adds a column to a table
        /// </summary>
        /// <param name="t">TableConf object for the table</param>
        /// <param name="dbName">Database name the table lives in</param>
        /// <param name="schema">Schema the table is part of</param>
        /// <param name="table">Table name</param>
        /// <param name="columnName">Column name to add</param>
        /// <param name="dataType">String representation of the column's data type</param>
        void AddColumn(TableConf t, string dbName, string schema, string table, string columnName, string dataType);

        /// <summary>
        /// Drops a column from a table
        /// </summary>
        /// <param name="t">TableConf object for the table</param>
        /// <param name="dbName">Database name the table lives in</param>
        /// <param name="schema">Schema the table is part of</param>
        /// <param name="table">Table name</param>
        /// <param name="columnName">Column name to drop</param>
        void DropColumn(TableConf t, string dbName, string schema, string table, string columnName);

        void CreateTableInfoTable(string p, long p_2);

        void PublishTableInfo(string dbName, TableConf t, long CTID, long expectedRows);

        void ApplyTableChanges(TableConf table, TableConf archiveTable, string dbName, Int64 ctid, string CTDBName);

        void Consolidate(string ctTableName, string consolidatedTableName, string dbName, string schemaName);

        void RemoveDuplicatePrimaryKeyChangeRows(TableConf table, string consolidatedTableName, string dbName);

        void CreateHistoryTable(ChangeTable t, string slaveCTDB);

        void CopyIntoHistoryTable(ChangeTable t, string slaveCTDB);
    }
}
