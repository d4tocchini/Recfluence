﻿using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Mutuo.Etl.Db;
using Serilog;
using Snowflake.Data.Client;
using SysExtensions.Security;
using SysExtensions.Text;

namespace YtReader {
  public class SnowflakeCfg {
    public            NameSecret Creds     { get; set; } = new NameSecret();
    [Required] public string     Account   { get; set; }
    public            string     Warehouse { get; set; }
    public            string     Db        { get; set; }
    public            string     Schema    { get; set; }
    public            string     Role      { get; set; }
  }

  public class SnowflakeConnectionProvider {
    public SnowflakeCfg Cfg { get; }

    public SnowflakeConnectionProvider(SnowflakeCfg cfg) => Cfg = cfg;

    public async Task<LoggedConnection> OpenConnection(ILogger log, string db = null, string schema = null, string role = null) {
      var conn = Cfg.Connection(db, schema, role);
      await conn.OpenAsync();
      return conn.AsLogged(log);
    }
  }

  public static class SnowflakeConnectionEx {
    public static SnowflakeDbConnection Connection(this SnowflakeCfg cfg, string db = null, string schema = null, string role = null) =>
      new SnowflakeDbConnection
        {ConnectionString = cfg.Cs(db, schema, role), Password = cfg.Creds.SecureString()};

    public static string Cs(this SnowflakeCfg cfg, string db = null, string schema = null, string role = null) =>
      Cs(new (string name, string value)[] {
        ("account", cfg.Account),
        ("user", cfg.Creds.Name),
        ("db", db ?? cfg.Db),
        ("schema", schema ?? cfg.Schema),
        ("warehouse", cfg.Warehouse),
        ("role", role ?? cfg.Role)
      }.Where(v => v.value.HasValue()).ToArray());

    public static string Cs(params (string name, string value)[] values) => values.Join(";", v => $"{v.name}={v.value}");
  }
}