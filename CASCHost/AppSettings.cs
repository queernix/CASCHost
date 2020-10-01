﻿using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CASCHost
{
	public class AppSettings
	{
		public uint MinimumFileDataId { get; set; } // the minimum file id for new files
		public bool BNetAppSupport { get; set; } // create install and download files?
		public bool StaticMode { get; set; } // Build CDN file struct
        public string RebuildPassword { get; set; }

		public string HostDomain { get; set; } // accessible address of this server
		public string[] CDNs { get; set; } // custom CDNs i.e. local client CASC archive clone
		//public string SqlConnection { get; set; } // database connection string
		public string PatchUrl { get; set; } // offical blizzard patch url i.e. http://us.patch.battle.net:1119
		public string Locale { get; set; } // preferred locale for content

		public string[] DirectoryHash { get; set; } // hashes of directories for offline change detection

		public string WebRootDir {get; set;} //where the content goes

		public string MySQLHost { get; set; } 

		public string MySQLUid { get; set; }  

		public string MySQLPassword { get; set; }

		public string MySQLDatabase { get; set; }

		public ushort MySQLPort { get; set; } 

		public void Save(IHostingEnvironment env)
		{
			if (CDNs == null)
				CDNs = new string[0];

			// add to expando to include root node when saving
			var obj = new ExpandoObject() as IDictionary<string, Object>;
			obj.Add(GetType().Name, this);

			using (FileStream fs = new FileStream(Path.Combine(env.ContentRootPath, "appsettings.json"), FileMode.Create, FileAccess.Write, FileShare.Read))
			using (StreamWriter sw = new StreamWriter(fs))
			{
				sw.Write(JsonConvert.SerializeObject(obj, Formatting.Indented));
				sw.Flush();
			}
		}
	}
}
