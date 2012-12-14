﻿#region Using Statements
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Linq;
using System.Data;
using System.Diagnostics;
using Xunit;
using TeslaSQL.DataUtils;
#endregion


namespace TeslaSQL.Agents {
    //each agent (master, slave, etc.) should inherit this
    public abstract class Agent {

        public Config config;

        public IDataUtils sourceDataUtils;
        public IDataUtils destDataUtils;

        public Logger logger;

        public Agent() {
            //parameterless constructor used only for unit tests
        }

        public Agent(Config config, IDataUtils sourceDataUtils, IDataUtils destDataUtils) {
            this.config = config;
            this.sourceDataUtils = sourceDataUtils;
            this.destDataUtils = destDataUtils;
            this.logger = new Logger(config.logLevel, config.statsdHost, config.statsdPort, config.errorLogDB, sourceDataUtils);
        }

        public abstract void Run();

        public abstract void ValidateConfig();

        /// <summary>
        /// Set field list values for each table in the config
        /// </summary>
        /// <param name="Database">Database name to run on</param>
        /// <param name="tableConfArray">Array of tableconf objects to loop through and set field lists on</param>
        public void SetFieldLists(string database, TableConf[] tableConfArray, IDataUtils dataUtils) {
            Dictionary<string, bool> dict;
            foreach (TableConf t in tableConfArray) {
                try {
                    dict = dataUtils.GetFieldList(database, t.Name, t.schemaName);
                    SetFieldList(t, dict);
                } catch (Exception e) {
                    if (t.stopOnError) {
                        throw e;
                    } else {
                        logger.Log("Error setting field lists for table " + t.schemaName + "." + t.Name + ": " + e.Message + " - Stack Trace:" + e.StackTrace, LogLevel.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Set several field lists on a TableConf object using its config and an smo table object.
        /// </summary>
        /// <param name="t">A table configuration object</param>
        /// <param name="fields">Dictionary of field names with a bool for whether they are part of the primary key</param>
        public void SetFieldList(TableConf t, Dictionary<string, bool> fields) {
            Stopwatch st = new Stopwatch();
            st.Start();
            foreach (KeyValuePair<string, bool> c in fields) {
                if (t.columnList == null || t.columnList.Contains(c.Key, StringComparer.OrdinalIgnoreCase)) {
                    t.columns.Add(new TColumn(c.Key, c.Value));
                }
            }

            st.Stop();
            logger.Log("SetFieldList Elapsed time for table " + t.schemaName + "." + t.Name + ": " + Convert.ToString(st.ElapsedMilliseconds), LogLevel.Trace);
        }

        /// <summary>
        /// Given a table name and CTID, returns the CT table name
        /// </summary>
        /// <param name="table">Table name</param>
        /// <param name="CTID">Change tracking batch iD</param>
        /// <returns>CT table name</returns>
        public string CTTableName(string table, Int64 CTID) {
            return "tblCT" + table + "_" + Convert.ToString(CTID);
        }
    }
}
