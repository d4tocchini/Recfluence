﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Util;
using CliFx;
using CliFx.Attributes;
using Mutuo.Etl.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using Serilog;
using Serilog.Core;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Reflection;
using SysExtensions.Serialization;
using SysExtensions.Threading;

namespace Mutuo.Etl.Pipe {
  public class PipeRunOptions {
    public bool             ReturnOnStarted { get; set; }
    public PipeRunLocation? Location        { get; set; }

    /// <summary>when true, will fail if this pipe is already running</summary>
    public bool Exclusive { get; set; }
  }

  public static class Pipes {
    public static async Task<(PipeRunMetadata Metadata, TOut State)> Run<TOut, TPipeInstance>(this IPipeCtx ctx,
      Expression<Func<TPipeInstance, Task<TOut>>> expression, PipeRunOptions options = null, ILogger log = null) {
      var pipeCall = PipeMethodCall(expression);
      return await RunRootPipe<TOut>(ctx, pipeCall.Method.Name, pipeCall.ResolveArgs(), options, log ?? Logger.None);
    }

    public static async Task<PipeRunMetadata> Run<TPipeInstance>(this IPipeCtx ctx, Expression<Func<TPipeInstance, Task>> expression,
      PipeRunOptions options = null, ILogger log = null, CancellationToken cancel = default) {
      var pipeCall = PipeMethodCall(expression);
      var res = await RunRootPipe<object>(ctx, pipeCall.Method.Name, ResolveArgs(pipeCall), options, log ?? Logger.None, cancel);
      return res.Metadata;
    }

    /// <summary>Launches a root pipe</summary>
    /// <param name="ctx"></param>
    /// <param name="expression">a call to a pipe method. The arguments will be resolved and serialized</param>
    /// <param name="location"></param>
    /// <param name="log"></param>
    /// <param name="returnOnStarted"></param>
    public static async Task<(PipeRunMetadata Metadata, TOut State)> Run<TOut>(this IPipeCtx ctx, Expression<Func<Task<TOut>>> expression,
      PipeRunOptions options = null, ILogger log = null) {
      var pipeCall = PipeMethodCall(expression);
      var res = await RunRootPipe<TOut>(ctx, pipeCall.Method.Name, pipeCall.ResolveArgs(), options, log);
      return res;
    }

    /// <summary>Launches a root pipe</summary>
    public static async Task<PipeRunMetadata> Run(this IPipeCtx ctx, string pipeName, PipeRunOptions options = null,
      (string Name, object Value)[] args = null, ILogger log = null, CancellationToken cancel = default) =>
      (await RunRootPipe<object>(ctx, pipeName, SerializableArgs(args ?? new (string Name, object Value)[] { }), options, log, cancel)).Metadata;

    /// <summary>Launches a child pipe that works on a list of items</summary>
    /// <param name="expression">a call to a pipe method. The arguments will be resolved and serialized</param>
    public static async Task<IReadOnlyCollection<(PipeRunMetadata Metadata, TOut OutState)>> Process<TIn, TOut>(this IEnumerable<TIn> items, IPipeCtx ctx,
      Expression<Func<TIn[], Task<TOut>>> expression, PipeRunCfg runCfg = null, ILogger log = null, CancellationToken cancel = default) {
      var pipeCall = expression.Body as MethodCallExpression ?? throw new InvalidOperationException("The expression must be a call to a pipe method");
      if (pipeCall.Method.GetCustomAttribute<PipeAttribute>() == null)
        throw new InvalidOperationException($"given transform '{pipeCall.Method.Name}' must have a Pipe attribute");
      return await RunItemPipe<TIn, TOut>(items.ToArray(), ctx, pipeCall.Method.Name, pipeCall.ResolveArgs(), runCfg, log ?? Logger.None, cancel);
    }

    /// <summary>Launches a pipe that works on a batch of items</summary>
    public static async Task<IReadOnlyCollection<(PipeRunMetadata Metadata, object OutState)>> Process<TIn>(this IEnumerable<TIn> items,
      IPipeCtx ctx, string pipeName, (string Name, object Value)[] args, PipeRunCfg runCfg = null, ILogger log = null) =>
      await RunItemPipe<object, object>(items.Cast<object>().ToArray(), ctx, pipeName, SerializableArgs(args), runCfg, log);

    static async Task<(PipeRunMetadata Metadata, TOut OutState)> RunRootPipe<TOut>(this IPipeCtx ctx, string pipeName, PipeArg[] args,
      PipeRunOptions options = null, ILogger log = null, CancellationToken cancel = default) {
      options ??= new PipeRunOptions();
      log ??= Logger.None;

      var runId = PipeRunId.FromName(pipeName);
      await ctx.SaveInArg(args, runId, log);
      var pipeWorker = PipeWorker(ctx, options.Location);
      var md = pipeWorker is IPipeWorkerStartable s
        ? await s.Launch(ctx, runId, options.ReturnOnStarted, options.Exclusive, log, cancel)
        : await pipeWorker.Launch(ctx, runId, log, cancel);
      var state = await GetOutState<TOut>(ctx, log, md, options.ReturnOnStarted);
      return state;
    }

    /// <summary>Runs a pipe to process a list of work in batches on multiple containers. The transform is used to provide
    ///   strong typing, but may not actually be run locally.</summary>
    static async Task<IReadOnlyCollection<(PipeRunMetadata Metadata, TOut OutState)>> RunItemPipe<TIn, TOut>(this TIn[] items, IPipeCtx ctx,
      string pipeName, PipeArg[] args, PipeRunCfg runCfg = null, ILogger log = null, CancellationToken cancel = default) {
      log ??= Logger.None;
      var pipeMethods = PipeMethods(ctx);
      var (_, method) = pipeMethods[pipeName];
      if (method == null) throw new InvalidOperationException($"Can't find pipe {pipeName}");
      var isPipe = method.GetCustomAttribute<PipeAttribute>() != null;
      if (!isPipe) throw new InvalidOperationException($"given transform '{method.Name}' must be a pipe");

      // batch and create state for containers to read
      var groupRunId = PipeRunId.FromName(pipeName);
      runCfg ??= groupRunId.PipeCfg(ctx.PipeCfg);

      await ctx.SaveInArg(args, groupRunId, log);

      var batches = await items.Batch(runCfg.MinWorkItems, runCfg.MaxParallel)
        .Select((g, i) => (Id: new PipeRunId(pipeName, groupRunId.GroupId, i), In: g.ToArray()))
        .BlockFunc(async b => {
          await ctx.SaveInRows(b.In, b.Id, log);
          return b.Id;
        }, ctx.PipeCfg.Store.Parallel);

      var pipeWorker = PipeWorker(ctx);
      log.Debug("{PipeWorker} - launching batches {@batches}", pipeWorker.GetType().Name, batches);
      var res = pipeWorker is IPipeWorkerStartable s
        ? await s.Launch(ctx, batches, runCfg.ReturnOnStart, false, log, cancel: cancel)
        : await pipeWorker.Launch(ctx, batches, log, cancel: cancel);

      var hasOutState = typeof(TOut) != typeof(object) && !runCfg.ReturnOnStart;
      var outState = hasOutState ? await GetOutState() : res.Select(r => (Metadata: r, OutState: (TOut) default)).ToArray();
      var batchId = $"{pipeName}|{groupRunId.GroupId}";

      if (runCfg.ReturnOnStart)
        log.Debug("LaunchItemPipe Batch {BatchId} - Launched {Started}/{ContainersTotal} containers",
          batchId, res.Count(r => !r.Error), res.Count);
      else
        log.Debug("LaunchItemPipe Batch {BatchId} - Succeeded {Succeeded}/{ContainersTotal} succeeded/total",
          batchId, res.Count(r => !r.Error && r.State == ContainerState.Succeeded), res.Count);

      var failed = outState.Where(o => o.Metadata.Error).ToArray();
      if (failed.Any())
        log.Error("LaunchItemPipe Batch {BatchId} - {Batches} batches failed. Ids:{Ids}",
          batchId, failed.Length, failed.Select(f => f.Metadata.Id));

      return outState;

      async Task<IReadOnlyCollection<(PipeRunMetadata Metadata, TOut OutState)>> GetOutState() =>
        await res.BlockFunc(async b => await GetOutState<TOut>(ctx, log, b, runCfg.ReturnOnStart), ctx.PipeCfg.Store.Parallel);
    }

    static async Task<(PipeRunMetadata Metadata, TOut OutState)> GetOutState<TOut>(IPipeCtx ctx, ILogger log, PipeRunMetadata b, bool returnOnStart) {
      var state = !returnOnStart && !b.Error && typeof(TOut) != typeof(object) ? await GetOutState<TOut>(ctx, b.Id, log) : default;
      return (b, state);
    }

    static IPipeWorker PipeWorker(IPipeCtx ctx, PipeRunLocation? location = null) {
      location ??= ctx.PipeCfg.Location;
      IPipeWorker pipeWorker = location switch {
        PipeRunLocation.Container => ctx.Scope.Resolve<AzureContainers>(),
        PipeRunLocation.LocalContainer => ctx.Scope.Resolve<LocalPipeWorker>(),
        _ => new ThreadPipeWorker()
      };
      return pipeWorker;
    }

    /// <summary>Executes a pipe in this process. Assumes args/state has been created for this to run</summary>
    public static async Task DoPipeWork(this IPipeCtx ctx, PipeRunId id, CancellationToken cancel) {
      var pipeMethods = PipeMethods(ctx);

      var pipeName = id.Name;
      var pipeType = pipeMethods[pipeName];
      if (pipeType == default) throw new InvalidOperationException($"Could not find pipe {pipeName}");
      if (!pipeType.Method.ReturnType.IsAssignableTo<Task>()) throw new InvalidOperationException($"Pipe {pipeName} must be async");

      var pipeInstance = ctx.Scope.Resolve(pipeType.Type);
      var method = pipeType.Method;

      var pipeLog = ctx.Log.ForContext("Pipe", pipeName).ForContext("RunId", id);

      var loadInArgs = await LoadInArgs(ctx, id);
      var args = loadInArgs.ToDictionary(a => a.Name);

      if (cancel.IsCancellationRequested)
        return;

      var pipeParams = await method.GetParameters().BlockFunc(async p => {
        if (args.TryGetValue(p.Name ?? throw new NotImplementedException("parameters must have names"), out var arg))
          switch (arg.ArgMode) {
            case ArgMode.SerializableValue:
              return ChangeToType(arg.Value, p.ParameterType);
            case ArgMode.InRows: {
              var genericStateType = p.ParameterType.GenericTypeArguments.FirstOrDefault() ??
                                     throw new InvalidOperationException(
                                       $"Expecting arg method {pipeType.Type}.{method.Name} parameter {p.Name} to be IEnumerable<Type>");
              var rows = await typeof(Pipes).GetMethod(nameof(LoadInRows), new[] {typeof(IPipeCtx), typeof(PipeRunId)})
                .CallStaticGenericTask<IReadOnlyCollection<object>>(new[] {genericStateType}, ctx, id);
              return rows;
            }
          }
        return p.ParameterType == typeof(CancellationToken) ? cancel : ctx.Scope.Resolve(p.ParameterType);
      }, cancel: cancel);

      try {
        dynamic task = method.Invoke(pipeInstance, pipeParams.ToArray()) ??
                       throw new InvalidOperationException($"Method '{method.Name}' returned null, should be Task");
        if (method.ReturnType == typeof(Task)) {
          await task;
        }
        else {
          object pipeResult = await task;
          await ctx.SetOutState(pipeResult, id, pipeLog);
        }
      }
      catch (Exception ex) {
        ctx.Log.Error(ex, "Pipe {Pipe} failed with error: {Error}", pipeName, ex.Message);
        throw;
      }
    }

    static object ChangeToType(object value, Type type) {
      var needsConversion = !type.IsInstanceOfType(value);
      if (!needsConversion) return value;

      if (value == null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        return type.DefaultForType();
      if (value is string s && type.IsEnum)
        return s.ParseEnum(type);
      try {
        return Convert.ChangeType(value, type);
      }
      catch (Exception ex) {
        throw new NotImplementedException($"unable to convert arg deserialized as {value?.GetType()} to parameter type {type} : {ex.Message}", ex);
      }
    }

    public static IKeyedCollection<string, (Type Type, MethodInfo Method)> PipeMethods(this IPipeCtx ctx) =>
      ctx.AppCtx.Assemblies.SelectMany(a => a.GetLoadableTypes())
        .SelectMany(t => t.GetRuntimeMethods().Where(m => m.GetCustomAttribute<PipeAttribute>() != null).Select(m => (Type: t, Method: m)))
        .ToKeyedCollection(m => m.Method.Name);

    #region State

    public static string StatePath(this PipeRunId id) => $"{id.Name}/{id.GroupId}/{id.Num}";

    static string OutStatePath(this PipeRunId id) => $"{id.StatePath()}.OutState";
    static string InRowsPath(this PipeRunId id) => $"{id.StatePath()}.InRows";
    static string InArgPath(this PipeRunId id) => $"{id.Name}/{id.GroupId}/InArgs";

    static async Task<T> GetOutState<T>(this IPipeCtx ctx, PipeRunId id, ILogger log) =>
      await ctx.Store.Get<T>(id.OutStatePath(), log: log);

    static async Task SetOutState<T>(this IPipeCtx ctx, T state, PipeRunId id, ILogger log) =>
      await ctx.Store.Set(id.OutStatePath(), state, log: log);

    static readonly JsonSerializerSettings ArgJCfg = JsonExtensions.DefaultSettings()
      .ShallowWith(new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.All});

    static async Task SaveInArg(this IPipeCtx ctx, PipeArg[] args, PipeRunId id, ILogger log) {
      var path = $"{id.InArgPath()}.json";

      await ctx.Store.Save(path, args.ToJsonStream(ArgJCfg), log);
    }

    static async Task<PipeArg[]> LoadInArgs(this IPipeCtx ctx, PipeRunId id) {
      using var argStream = await ctx.Store.Load($"{id.InArgPath()}.json");
      return argStream.ToObject<PipeArg[]>(ArgJCfg);
    }

    static async Task SaveInRows<T>(this IPipeCtx ctx, IEnumerable<T> rows, PipeRunId id, ILogger log) {
      using var s = await rows.ToJsonlGzStream();
      await ctx.Store.Save($"{id.InRowsPath()}.jsonl.gz", s, log);
    }

    public static async Task<IReadOnlyCollection<T>> LoadInRows<T>(this IPipeCtx ctx, PipeRunId id) {
      using var stateStream = await ctx.Store.Load($"{id.InRowsPath()}.jsonl.gz");
      return stateStream.LoadJsonlGz<T>();
    }

    public static PipeRunCfg PipeCfg(this PipeRunId id, PipeAppCfg cfg) {
      PipeRunCfg pipeCfg = cfg.Pipes.FirstOrDefault(p => p.PipeName == id.Name);
      if (pipeCfg == null) return cfg.Default;

      var cfgJson = cfg.Default.ToJObject();
      cfgJson.Merge(pipeCfg.ToJObject(), new JsonMergeSettings {MergeNullValueHandling = MergeNullValueHandling.Ignore});
      var mergedCfg = cfgJson.ToObject<PipeRunCfg>();
      return mergedCfg;
    }

    #endregion

    #region Args

    static PipeArg[] ResolveArgs(this MethodCallExpression methodCall) {
      if (methodCall.Method.GetCustomAttribute<PipeAttribute>() == null)
        throw new InvalidOperationException($"given transform '{methodCall.Method.Name}' must have a Pipe attribute");
      var byPosition = methodCall.Method.GetParameters().ToKeyedCollection(p => p.Position);
      var res = methodCall.Arguments.Select((a, i) => {
        var name = byPosition[i]?.Name;
        var arg = a switch {
          ConstantExpression c => new PipeArg(name, ArgMode.SerializableValue, c.Value),
          MethodCallExpression m => IsArgInject(m)
            ? new PipeArg(name, ArgMode.Inject)
            : throw new NotImplementedException("resolving args through methods unsupported"),
          MemberExpression m => new PipeArg(name, ArgMode.SerializableValue, m.GetValue()),
          // Parameter's are the left side of the lambda (myParam) => myParam.doThing()
          ParameterExpression p => p.Type.IsEnumerable() ? new PipeArg(name, ArgMode.InRows) : new PipeArg(name, ArgMode.Inject),
          UnaryExpression u when u.Operand is MemberExpression m => new PipeArg(name, ArgMode.SerializableValue, GetValue(m)),
          _ => throw new NotImplementedException($"resolving args through expression {a} not supported")
        };
        return arg;
      }).ToArray();
      return res;
    }

    static bool IsArgInject(MethodCallExpression m) => m.Method.Name == nameof(PipeArg.Inject) && m.Method.DeclaringType == typeof(PipeArg);

    static object GetValue(this MemberExpression member) =>
      Expression.Lambda<Func<object>>(Expression.Convert(member, typeof(object))).Compile()();

    static PipeArg[] SerializableArgs((string Name, object Value)[] args) =>
      args.Select(a => new PipeArg {Name = a.Name, Value = a.Value, ArgMode = ArgMode.SerializableValue}).ToArray();

    static MethodCallExpression PipeMethodCall(LambdaExpression expression) =>
      expression.Body as MethodCallExpression ?? throw new InvalidOperationException("The expression must be a call to a pipe method");

    #endregion

    public static string PipeTag(this SemVersion version) => version.ToString();
  }

  public class PipeArg {
    public PipeArg(string name, ArgMode argMode, object value = null) {
      Name = name;
      ArgMode = argMode;
      Value = value;
    }

    public PipeArg() { }

    public string  Name    { get; set; }
    public ArgMode ArgMode { get; set; }

    /// <summary>The value of the arg. We rely on the serializer embedding the type name to deserialize into the instance type</summary>
    public object Value { get; set; }

    /// <summary>used in expressions, this represents an arugment to a pipe that should be resolved</summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T Inject<T>() => default;
  }

  public enum ArgMode {
    SerializableValue,
    InRows,
    Inject
  }

  public enum ExitCode {
    Success = 0,
    Error   = 10
  }

  /// <summary>The application entrypoint for inner pipe dependencies and parallel tasks. Add this to your CLI as a verb Not
  ///   intended to be called by user. Seperately provide your own high level entrypoints with explicit parameters and help.</summary>
  public abstract class PipeCmdArgs : ICommand {
    [CommandOption('r', Description = "The pipe name, or the runId in the format Pipe|Group|Num.")]
    public string RunId { get; set; }

    [CommandOption('l', Description = "The location to run the pipe Local/Container/LocalContainer")]
    public PipeRunLocation? Location { get; set; } = PipeRunLocation.Local;

    public abstract ValueTask ExecuteAsync(IConsole console);
  }

  /// <summary>Decorate any types that contain pipe functions. The parameters will be populated from either the InState
  ///   deserialized form blob storage, or from command line parameters, or from ILifetimeScope</summary>
  [AttributeUsage(AttributeTargets.Method)]
  public class PipeAttribute : Attribute { }
}