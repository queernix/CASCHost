﻿using CASCEdit;
using CASCEdit.Configs;
using CASCEdit.Helpers;
using CASCEdit.Structs;
using Microsoft.AspNetCore.Hosting;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CASCHost
{
	public class Cache : ICache
	{
		public string Version { get; private set; }
		public HashSet<string> ToPurge { get; private set; }
		public IReadOnlyCollection<CacheEntry> Entries => RootFiles.Values;
		public uint MaxId => RootFiles.Values.Count == 0 ? 0 : RootFiles.Values.Max(x => x.FileDataId);

		public bool HasFiles => RootFiles.Count > 0;
		public bool HasId(uint fileid) => RootFiles.Any(x => x.Value.FileDataId == fileid);

		private IHostingEnvironment env;
		private string Patchpath => Path.Combine(CASContainer.Settings.OutputPath, ".patch");
		private Dictionary<string, CacheEntry> RootFiles;
		private Queue<string> queries = new Queue<string>();
		private bool firstrun = true;

		private MySqlConnectionStringBuilder mysqlstring;
		
		//private MySqlConnection mysqlcon;

		public Cache(IHostingEnvironment environment)
		{
			env = environment;
			Startup.Logger.LogInformation("Loading cache...");

			mysqlstring = new MySqlConnectionStringBuilder();
			mysqlstring.Server = Startup.Settings.MySQLHost;
			mysqlstring.Port = Startup.Settings.MySQLPort;
			mysqlstring.UserID = Startup.Settings.MySQLUid;
			mysqlstring.Password = Startup.Settings.MySQLPassword;
			mysqlstring.Database = Startup.Settings.MySQLDatabase;

			Load();
		}


		public void AddOrUpdate(CacheEntry item)
		{
			if(firstrun)
			{
				Clean();
				firstrun = false;
			}

			if (RootFiles == null)
				Load();

			// Update value
			if (RootFiles.ContainsKey(item.Path))
			{
				// Matching
				if (RootFiles[item.Path] == item)
					return;

				RootFiles[item.Path] = item;

				queries.Enqueue(string.Format(REPLACE_RECORD, MySqlHelper.EscapeString(item.Path), item.FileDataId, item.NameHash, item.CEKey, item.EKey));
				return;
			}

			// Matching Id - Ignore root/encoding
			if (item.FileDataId > 0 && RootFiles.Values.Any(x => x.FileDataId == item.FileDataId))
			{
				var existing = RootFiles.Where(x => x.Value.FileDataId == item.FileDataId).ToArray();
				foreach (var ex in existing)
				{
					queries.Enqueue(string.Format(DELETE_RECORD, MySqlHelper.EscapeString(item.Path)));
					RootFiles.Remove(ex.Key);
				}
			}

			// Add
			RootFiles.Add(item.Path, item);

			queries.Enqueue(string.Format(REPLACE_RECORD, MySqlHelper.EscapeString(item.Path), item.FileDataId, item.NameHash, item.CEKey, item.EKey));
		}

		public void Remove(string file)
		{
			if (RootFiles.ContainsKey(file))
			{
				queries.Enqueue(string.Format(DELETE_RECORD, MySqlHelper.EscapeString(RootFiles[file].Path)));
				RootFiles.Remove(file);
			}
		}

		public void Save()
		{
			BatchTransaction();
		}

		public void Load()
		{
			if (RootFiles != null)
				return;

			RootFiles = new Dictionary<string, CacheEntry>();
			LoadOrCreate();
		}

		public void Clean()
		{
			//Delete previous Root and Encoding
			if (RootFiles.ContainsKey("__ROOT__") && File.Exists(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ROOT__"].EKey.ToString(), "data"))))
				File.Delete(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ROOT__"].EKey.ToString(), "data")));
			if (RootFiles.ContainsKey("__ENCODING__") && File.Exists(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ENCODING__"].EKey.ToString(), "data"))))
				File.Delete(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ENCODING__"].EKey.ToString(), "data")));
		}


		#region SQL Methods
		private void LoadOrCreate()
		{
			Version = new SingleConfig(Path.Combine(env.WebRootPath, "SystemFiles", ".build.info"), "Active", "1")["Version"];
			using (MySqlConnection connection = new MySqlConnection(mysqlstring.ToString())){
				using (MySqlCommand command = new MySqlCommand())
				{
					try
					{
						connection.Open();
						command.Connection = connection;

						// create data table
						command.CommandText = CREATE_DATA_TABLE;
						command.ExecuteNonQuery();

						// load data
						command.CommandText = LOAD_DATA;
						ReadAll(command.ExecuteReader());

						// purge old data
						command.CommandText = PURGE_RECORDS;
						command.ExecuteNonQuery();
					}
					catch(MySqlException ex)
					{
						Startup.Logger?.LogFile(ex.Message);
						Startup.Logger?.LogFile("MySqlException Number: " + ex.Number.ToString());
						Startup.Logger?.LogAndThrow(CASCEdit.Logging.LogType.Critical, "Unable to connect to the database.");
					}
				}
			}
		}

		private void ReadAll(DbDataReader reader)
		{
			ToPurge = new HashSet<string>();

			using (reader)
			{
				while (reader.Read())
				{
					CacheEntry entry = new CacheEntry()
					{
						Path = reader.GetFieldValue<string>(1),
						FileDataId = reader.GetFieldValue<uint>(2),
						NameHash = reader.GetFieldValue<ulong>(3),
						CEKey = new MD5Hash(reader.GetFieldValue<string>(4).ToByteArray()),
						EKey = new MD5Hash(reader.GetFieldValue<string>(5).ToByteArray())
					};

					// keep files that still exist or are special and not flagged to be deleted
					bool keep = File.Exists(Path.Combine(env.WebRootPath, "Data", entry.Path)) && reader.IsDBNull(6);
					if (keep || entry.FileDataId == 0)
					{
						RootFiles.Add(entry.Path, entry);
					}
					else if (reader.IsDBNull(6)) // needs to be marked for purge
					{
						queries.Enqueue(string.Format(DELETE_RECORD, MySqlHelper.EscapeString(entry.Path)));
						Startup.Logger.LogInformation($"{entry.Path} missing. Marked for removal.");
						ToPurge.Add(entry.Path);
					}
					else if (reader.GetFieldValue<DateTime>(6) <= DateTime.Now.Date) // needs to be purged
					{
						ToPurge.Add(entry.Path);

						string cdnpath = Helper.GetCDNPath(entry.EKey.ToString(), "", "", Startup.Settings.StaticMode);
						string filepath = Path.Combine(env.WebRootPath, "Output", cdnpath);

						if (File.Exists(filepath))
							File.Delete(filepath);
						if (File.Exists(Path.Combine(env.WebRootPath, "Data", entry.Path)))
							File.Delete(Path.Combine(env.WebRootPath, "Data", entry.Path));
					}
				}

				reader.Close();
			}

			BatchTransaction();
		}

		private void BatchTransaction()
		{
			if (queries.Count == 0)
				return;

			Startup.Logger.LogInformation("Bulk updating database.");

			StringBuilder sb = new StringBuilder();
			while (queries.Count > 0)
			{
				sb.Clear();

				int count = Math.Min(queries.Count, 2500); // limit queries per transaction
				for (int i = 0; i < count; i++)
					sb.AppendLine(queries.Dequeue());

				try
				{
					using (MySqlConnection connection = new MySqlConnection(mysqlstring.ToString())) {
						using (MySqlCommand command = new MySqlCommand(sb.ToString(), connection))
						{
							connection.Open();
							command.ExecuteNonQuery();
						}
					}
				}
				catch (MySqlException ex)
				{
					Startup.Logger.LogError("SQLERR: " + ex.Message);
					Startup.Logger.LogFile("SQLERR: " + ex.Message);
					Startup.Logger.LogFile("SQL ERR Number: " + ex.Number.ToString());
				}
			}
		}

		#endregion

		#region SQL Strings

		private const string CREATE_DATA_TABLE = "CREATE TABLE IF NOT EXISTS `root_entries` (                     " +
												 " `Id` BIGINT NOT NULL AUTO_INCREMENT,                           " +
												 " `Path` VARCHAR(1024),                                          " +
												 " `FileDataId` INT UNSIGNED,                                     " +
												 " `Hash` BIGINT UNSIGNED,                                        " +
												 " `MD5` VARCHAR(32),                                             " +
												 " `BLTE` VARCHAR(32),                                            " +
												 " `PurgeAt` DATE NULL,                                           " +
												 " PRIMARY KEY(`Id`),                                             " +
												 " UNIQUE INDEX `Path` (`Path`)                                   " +
												 ") COLLATE = 'utf8_general_ci' ENGINE=InnoDB ROW_FORMAT=DYNAMIC; ";

		private const string LOAD_DATA =      "SELECT * FROM `root_entries`;";

		private const string REPLACE_RECORD = "INSERT INTO `root_entries` (`Path`, `FileDataId`, `Hash`, `MD5`, `BLTE`, `PurgeAt`) VALUES ('{0}', '{1}', '{2}', '{3}', '{4}', NULL) " +
                                              "ON DUPLICATE KEY UPDATE `FileDataId` = VALUES(`FileDataId`), `Hash` = VALUES(`Hash`), `MD5` = VALUES(`MD5`), `BLTE` = VALUES(`BLTE`), `PurgeAt` = VALUES(`PurgeAt`);";

		private const string DELETE_RECORD =  "UPDATE `root_entries` SET `PurgeAt` = DATE_ADD(CAST(NOW() AS DATE), INTERVAL 1 WEEK) WHERE `Path` = '{0}'; ";

		private const string PURGE_RECORDS =  "DELETE FROM `root_entries` WHERE `PurgeAt` < CAST(NOW() AS DATE); ";

		#endregion

	}
}
