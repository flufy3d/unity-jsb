﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Reflection;

namespace QuickJS
{
    using Native;
    using Binding;
    using Utils;
    using Module;

    public partial class ScriptRuntime
    {
        private class ScriptContextRef
        {
            public int next;
            public ScriptContext target;
        }

        public event Action<ScriptRuntime> OnDestroy;
        public event Action<ScriptRuntime> OnInitializing;
        public event Action<ScriptRuntime> OnInitialized;
        public event Action<ScriptRuntime> OnMainModuleLoaded;

        /// <summary>
        /// this event will be raised after debugger connected if debug server is used, otherwise it will be raised immediately after OnInitialized
        /// </summary>
        public event Action<ScriptRuntime> OnDebuggerConnected;
        public event Action<int> OnAfterDestroy;
        public event Action OnUpdate;
        public event Action<ScriptContext, string> OnScriptReloading;
        public event Action<ScriptContext, string> OnScriptReloaded;
        public Func<JSContext, string, string, int, string> OnSourceMap;

        // private Mutext _lock;
        private JSRuntime _rt;
        private int _runtimeId;
        private bool _withStacktrace;
        private IScriptLogger _logger;
        private int _freeContextSlot = -1;
        // private ReaderWriterLockSlim _rwlock;
        private List<ScriptContextRef> _contextRefs = new List<ScriptContextRef>();
        private ScriptContext _mainContext;
        private Queue<JSAction> _pendingActions = new Queue<JSAction>();

        private int _mainThreadId;

        private IFileSystem _fileSystem;
        private IPathResolver _pathResolver;
        private List<IModuleResolver> _moduleResolvers = new List<IModuleResolver>();
        private Dictionary<Type, ProxyModuleRegister> _allProxyModuleRegisters = new Dictionary<Type, ProxyModuleRegister>();
        private ObjectCache _objectCache;
        private ITypeDB _typeDB;
        private TimerManager _timerManager;
        private IO.IByteBufferAllocator _byteBufferAllocator;
        private Utils.AutoReleasePool _autorelease;
        private IAsyncManager _asyncManager;

        private bool _isValid; // destroy 调用后立即 = false
        private bool _isRunning;
        private bool _isInitialized;
        private bool _isWorker;
        private bool _isStaticBinding;

        public bool withStacktrace
        {
            get { return _withStacktrace; }
            set { _withStacktrace = value; }
        }

        public bool isInitialized { get { return _isInitialized; } }

        public bool isWorker { get { return _isWorker; } }

        public int id { get { return _runtimeId; } }

        public bool isRunning { get { return _isRunning; } }

        public bool isValid { get { return _isValid; } }

        public bool isStaticBinding { get { return _isStaticBinding; } }

        public ScriptRuntime(int runtimeId)
        {
            _runtimeId = runtimeId;
            _isWorker = false;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public IAsyncManager GetAsyncManager()
        {
            return _asyncManager;
        }

        public IFileSystem GetFileSystem()
        {
            return _fileSystem;
        }

        public IPathResolver GetPathResolver()
        {
            return _pathResolver;
        }

        public void AddSearchPath(string path)
        {
            _pathResolver.AddSearchPath(path);
        }

        public void AddTypeReference(ProxyModuleRegister proxy, Type type, ModuleExportsBind bind, bool preload, params string[] ns)
        {
            _allProxyModuleRegisters[type] = proxy;
            proxy.Add(type, bind, preload, ns);
        }

        public bool TryLoadType(ScriptContext context, Type type)
        {
            ProxyModuleRegister proxy;
            if (_allProxyModuleRegisters.TryGetValue(type, out proxy) && proxy.LoadType(context, type))
            {
                return true;
            }

            return false;
        }

        public JSValue _LoadType(ScriptContext context, string module_id, string topLevelNamespace)
        {
            var reg = FindModuleResolver<StaticModuleResolver>()?.GetModuleRegister<ProxyModuleRegister>(module_id);
            //TODO improve these dirty code
            return reg != null ? reg._LoadType(context, topLevelNamespace) : JSApi.JS_UNDEFINED;
        }

        // 添加默认 resolver
        public void AddModuleResolvers()
        {
            AddModuleResolver(new StaticModuleResolver());
            AddModuleResolver(new JsonModuleResolver());
            AddModuleResolver(new SourceModuleResolver(new Utils.DefaultJsonConverter()));
        }

        public T AddModuleResolver<T>(T moduleResolver)
        where T : IModuleResolver
        {
            _moduleResolvers.Add(moduleResolver);
            return moduleResolver;
        }

        public T FindModuleResolver<T>()
        {
            for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
            {
                var resolver = _moduleResolvers[i];
                if (resolver is T)
                {
                    return (T)resolver;
                }
            }
            return default(T);
        }

        public string ResolveFilePath(string parent_module_id, string module_id)
        {
            string resolved_id;
            for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
            {
                var resolver = _moduleResolvers[i];
                if (resolver.ResolveModule(_fileSystem, _pathResolver, parent_module_id, module_id, out resolved_id))
                {
                    return resolved_id;
                }
            }

            return null;
        }

        public string ResolveModuleId(ScriptContext context, string parent_module_id, string module_id)
        {
            if (module_id != null)
            {
                for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
                {
                    var resolver = _moduleResolvers[i];
                    string resolved_id;
                    if (resolver.ResolveModule(_fileSystem, _pathResolver, parent_module_id, module_id, out resolved_id))
                    {
                        return resolved_id;
                    }
                }
            }

            return null;
        }

        public void ResolveModule(string module_id)
        {
            ResolveModule(module_id, false);
        }

        public void ResolveModule(string module_id, bool set_as_main)
        {
            if (!string.IsNullOrEmpty(module_id))
            {
                var rval = ResolveModule(_mainContext, "", module_id, set_as_main);
                if (rval.IsException())
                {
                    JSNative.print_exception(_mainContext, _logger, LogLevel.Error, "failed to load module: " + module_id);
                }
                else
                {
                    JSApi.JSB_FreeValueRT(_rt, rval);
                }
            }
        }

        public JSValue ResolveModule(ScriptContext context, string parent_module_id, string module_id, bool set_as_main)
        {
            var ctx = (JSContext)context;
            for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
            {
                var resolver = _moduleResolvers[i];
                string resolved_id;
                if (resolver.ResolveModule(_fileSystem, _pathResolver, parent_module_id, module_id, out resolved_id))
                {
                    // 如果目标模块在 reloading 列表中, 直接进入重载逻辑
                    JSValue exports_obj;
                    if (TryGetModuleForReloading(context, resolver, resolved_id, out exports_obj))
                    {
                        return exports_obj;
                    }

                    // 如果已经在模块缓存中, 直接返回
                    JSValue module_obj;
                    if (context.LoadModuleCache(resolved_id, out module_obj))
                    {
                        exports_obj = JSApi.JS_GetProperty(ctx, module_obj, context.GetAtom("exports"));
                        JSApi.JS_FreeValue(ctx, module_obj);
                        return exports_obj;
                    }

                    // 载入新模块
                    return resolver.LoadModule(context, parent_module_id, resolved_id, set_as_main);
                }
            }

            return ctx.ThrowInternalError($"module can not be resolved ({module_id})");
        }

        public bool ReloadModule(ScriptContext context, string resolved_id)
        {
            JSContext ctx = context;
            for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
            {
                var resolver = _moduleResolvers[i];
                JSValue exports_obj;
                if (resolver.ContainsModule(_fileSystem, _pathResolver, resolved_id) && TryGetModuleForReloading(context, resolver, resolved_id, out exports_obj))
                {
                    JSApi.JS_FreeValue(ctx, exports_obj);
                    return true;
                }
            }

            return false;
        }

        private bool TryGetModuleForReloading(ScriptContext context, IModuleResolver resolver, string resolved_id, out JSValue exports_obj)
        {
            JSValue module_obj;
            if (context.TryGetModuleForReloading(resolved_id, out module_obj))
            {
                RaiseScriptReloadingEvent_nothrow(context, resolved_id);
                if (resolver.ReloadModule(context, resolved_id, module_obj, out exports_obj))
                {
                    RaiseScriptReloadedEvent_nothrow(context, resolved_id);
                    JSApi.JS_FreeValue(context, module_obj);
                    return true;
                }

                JSApi.JS_FreeValue(context, module_obj);
            }

            exports_obj = JSApi.JS_UNDEFINED;
            return false;
        }

        private void RaiseScriptReloadingEvent_nothrow(ScriptContext context, string resolved_id)
        {
            try
            {
                OnScriptReloading?.Invoke(context, resolved_id);
                context.RaiseScriptReloadingEvent_throw(resolved_id);
            }
            catch (Exception exception)
            {
                _logger?.WriteException(exception);
            }
        }

        private void RaiseScriptReloadedEvent_nothrow(ScriptContext context, string resolved_id)
        {
            try
            {
                OnScriptReloaded?.Invoke(context, resolved_id);
                context.RaiseScriptReloadedEvent_throw(resolved_id);
            }
            catch (Exception exception)
            {
                _logger?.WriteException(exception);
            }
        }

        [MonoPInvokeCallback(typeof(JSWaitingForDebuggerCFunction))]
        private static void _RunIfWaitingForDebugger(JSContext ctx)
        {
            var runtime = ScriptEngine.GetRuntime(ctx);
            if (runtime != null)
            {
                // wait only once
                JSApi.JS_SetWaitingForDebuggerFunc(ctx, null);
                runtime.RaiseDebuggerConnectedEvent();
            }
        }

        // 通用析构函数
        [MonoPInvokeCallback(typeof(JSGCObjectFinalizer))]
        public static void class_finalizer(JSRuntime rt, JSPayloadHeader header)
        {
            if (header.type_id == BridgeObjectType.ObjectRef)
            {
                var objectCache = ScriptEngine.GetObjectCache(rt);
                if (objectCache != null)
                {
                    try
                    {
                        objectCache.RemoveObject(header.value);
                    }
                    catch (Exception exception)
                    {
                        ScriptEngine.GetLogger(rt)?.WriteException(exception);
                    }
                }
            }
        }

        public void Initialize(ScriptRuntimeArgs args)
        {
            var fileSystem = args.fileSystem;
            if (fileSystem == null)
            {
                throw new NullReferenceException(nameof(fileSystem));
            }

#if !JSB_WITH_V8_BACKEND
            args.withDebugServer = false;
#endif
            args.asyncManager.Initialize(_mainThreadId);

            _isValid = true;
            _isRunning = true;
            _isStaticBinding = DefaultBinder.IsStaticBinding(args.binder);
            _logger = args.logger;
            // _rwlock = new ReaderWriterLockSlim();
            _rt = JSApi.JSB_NewRuntime(class_finalizer);
            JSApi.JS_SetHostPromiseRejectionTracker(_rt, JSNative.PromiseRejectionTracker, IntPtr.Zero);
#if UNITY_EDITOR
            JSApi.JS_SetInterruptHandler(_rt, _InterruptHandler, IntPtr.Zero);
#else
            if (isWorker)
            {
                JSApi.JS_SetInterruptHandler(_rt, _InterruptHandler, IntPtr.Zero);
            }
#endif
            JSApi.JSB_SetRuntimeOpaque(_rt, (IntPtr)_runtimeId);
#if !JSB_WITH_V8_BACKEND
            JSApi.JS_SetModuleLoaderFunc(_rt, module_normalize, module_loader, IntPtr.Zero);
#endif
            CreateContext(args.withDebugServer, args.debugServerPort);

            _pathResolver = args.pathResolver;
            _asyncManager = args.asyncManager;
            _byteBufferAllocator = args.byteBufferAllocator;
            _autorelease = new Utils.AutoReleasePool();
            _fileSystem = fileSystem;
            _objectCache = new ObjectCache(_logger);
            _timerManager = new TimerManager(_logger);
            _typeDB = new TypeDB(this, _mainContext);
#if !JSB_UNITYLESS
            _typeDB.AddType(typeof(Unity.JSBehaviour), JSApi.JS_UNDEFINED);
            _typeDB.AddType(typeof(Unity.JSScriptableObject), JSApi.JS_UNDEFINED);
#endif
#if UNITY_EDITOR
            _typeDB.AddType(Values.FindType("QuickJS.Unity.JSEditorWindow"), JSApi.JS_UNDEFINED);
            _typeDB.AddType(Values.FindType("QuickJS.Unity.JSBehaviourInspector"), JSApi.JS_UNDEFINED);
#endif

            // await Task.Run(() => runner.OnBind(this, register));
            try
            {
                args.binder?.Invoke(this);
            }
            catch (Exception exception)
            {
                _logger?.WriteException(exception);
            }

            var register = _mainContext.CreateTypeRegister();
            if (!_isWorker)
            {
                JSWorker.Bind(register);
            }
            TimerManager.Bind(register);
            register.Finish();

            AddStaticModule("jsb", ScriptContext.Bind);
            // FindModuleResolver<StaticModuleResolver>().Warmup(_mainContext);

#if !JSB_UNITYLESS
            //TODO may be changed in the future
            var plover = UnityEngine.Resources.Load<UnityEngine.TextAsset>("plover.js");
            if (plover != null)
            {
                _mainContext.EvalSource(plover.text, "plover.js");
            }
            else
            {
                _logger?.Write(LogLevel.Error, "failed to load plover.js from Resources");
            }
#endif

            if (!args.withDebugServer || !args.waitingForDebugger || args.debugServerPort <= 0 || JSApi.JS_IsDebuggerConnected(_mainContext) == 1)
            {
                RaiseDebuggerConnectedEvent();
            }
            else
            {
                _logger?.Write(LogLevel.Info, "[EXPERIMENTAL] Waiting for debugger...");
                JSApi.JS_SetWaitingForDebuggerFunc((JSContext)_mainContext, _RunIfWaitingForDebugger);
            }
        }

        private void RaiseDebuggerConnectedEvent()
        {
            if (!_isInitialized)
            {
                _isInitialized = true;
                OnInitializing?.Invoke(this);
                OnInitialized?.Invoke(this);
            }
            OnDebuggerConnected?.Invoke(this);
        }

        [MonoPInvokeCallback(typeof(JSInterruptHandler))]
        private static unsafe int _InterruptHandler(JSRuntime rt, IntPtr opaque)
        {
            var runtime = ScriptEngine.GetRuntime(rt);
            return runtime != null && runtime._isRunning ? 0 : 1;
        }

        public void AddStaticModule(string module_id, ModuleExportsBind bind)
        {
            FindModuleResolver<StaticModuleResolver>().AddStaticModule(module_id, bind);
        }

        public void AddStaticModule(string module_id, RawModuleBind bind)
        {
            FindModuleResolver<StaticModuleResolver>().AddStaticModule(module_id, bind);
        }

        public void AddStaticModule(string module_id, JSValue rawValue)
        {
            FindModuleResolver<StaticModuleResolver>().AddStaticModule(module_id, new ValueModuleRegister(this, rawValue));
        }

        public void AddStaticModule(string module_id, IModuleRegister register)
        {
            FindModuleResolver<StaticModuleResolver>().AddStaticModule(module_id, register);
        }

        // 用于静态绑定代码注册绑定模块
        public ProxyModuleRegister AddStaticModuleProxy(string module_id, Action<ScriptRuntime, ProxyModuleRegister> proxyReg = null)
        {
            var proxy = new ProxyModuleRegister(this, module_id);

            FindModuleResolver<StaticModuleResolver>().AddStaticModule(module_id, proxy);
            proxyReg?.Invoke(this, proxy);
            return proxy;
        }

        public ScriptRuntime CreateWorker()
        {
            if (isWorker)
            {
                throw new Exception("cannot create a worker inside a worker");
            }

            var runtime = ScriptEngine.CreateRuntime();

            runtime._isWorker = true;
            runtime.Initialize(new ScriptRuntimeArgs()
            {
                fileSystem = _fileSystem,
                pathResolver = _pathResolver,
                asyncManager = _asyncManager,
                logger = _logger,
                byteBufferAllocator = new IO.ByteBufferPooledAllocator(),
            });
            return runtime;
        }

        public void AutoRelease(Utils.IReferenceObject referenceObject)
        {
            _autorelease.AutoRelease(referenceObject);
        }

        public IO.IByteBufferAllocator GetByteBufferAllocator()
        {
            return _byteBufferAllocator;
        }

        public TimerManager GetTimerManager()
        {
            return _timerManager;
        }

        public IScriptLogger GetLogger()
        {
            return _logger;
        }

        public ITypeDB GetTypeDB()
        {
            return _typeDB;
        }

        public void ReplaceTypeDB(ITypeDB newTypeDB)
        {
            _typeDB = newTypeDB;
        }

        public Utils.ObjectCache GetObjectCache()
        {
            return _objectCache;
        }

        private ScriptContext CreateContext(bool withDebugServer, int debugServerPort)
        {
            // _rwlock.EnterWriteLock();
            ScriptContextRef freeEntry;
            int slotIndex;
            if (_freeContextSlot < 0)
            {
                freeEntry = new ScriptContextRef();
                slotIndex = _contextRefs.Count;
                _contextRefs.Add(freeEntry);
                freeEntry.next = -1;
            }
            else
            {
                slotIndex = _freeContextSlot;
                freeEntry = _contextRefs[slotIndex];
                _freeContextSlot = freeEntry.next;
                freeEntry.next = -1;
            }

            var context = new ScriptContext(this, slotIndex + 1, withDebugServer, debugServerPort);

            freeEntry.target = context;
            if (_mainContext == null)
            {
                _mainContext = context;
            }
            // _rwlock.ExitWriteLock();

            context.RegisterBuiltins();
            return context;
        }

        /// <summary>
        /// (internal use only)
        /// </summary>
        public void RemoveContext(ScriptContext context)
        {
            // _rwlock.EnterWriteLock();
            var id = context.id;
            if (id > 0)
            {
                var index = id - 1;
                var entry = _contextRefs[index];
                entry.next = _freeContextSlot;
                entry.target = null;
                _freeContextSlot = index;
            }
            // _rwlock.ExitWriteLock();
        }

        public ScriptContext GetMainContext()
        {
            return _mainContext;
        }

        public ScriptContext GetContext(JSContext ctx)
        {
            // _rwlock.EnterReadLock();
            ScriptContext context = null;
            var id = (int)JSApi.JS_GetContextOpaque(ctx);
            if (id > 0)
            {
                var index = id - 1;
                if (index < _contextRefs.Count)
                {
                    context = _contextRefs[index].target;
                }
            }
            // _rwlock.ExitReadLock();
            return context;
        }

        private static void _FreeValueAction(ScriptRuntime rt, JSAction action)
        {
            JSApi.JSB_FreeValueRT(rt, action.value);
        }

        private static void _FreeValueAndDelegationAction(ScriptRuntime rt, JSAction action)
        {
            var cache = rt.GetObjectCache();
            cache.RemoveDelegate(action.value);
            JSApi.JSB_FreeValueRT(rt, action.value);
        }

        private static void _FreeValueAndScriptValueAction(ScriptRuntime rt, JSAction action)
        {
            var cache = rt.GetObjectCache();
            cache.RemoveScriptValue(action.value);
            JSApi.JSB_FreeValueRT(rt, action.value);
        }

        private static void _FreeValueAndScriptPromiseAction(ScriptRuntime rt, JSAction action)
        {
            var cache = rt.GetObjectCache();
            cache.RemoveScriptPromise(action.value);
            JSApi.JSB_FreeValueRT(rt, action.value);
        }

        private bool EnqueuePendingAction(JSAction action)
        {
            _pendingActions.Enqueue(action);
            if (_runtimeId < 0)
            {
                _logger?.Write(LogLevel.Error, "fatal error: enqueue pending action after the runtime shutdown");
                return false;
            }
            return true;
        }

        // 可在 GC 线程直接调用此方法
        public bool FreeDelegationValue(JSValue value, string debugInfo = null)
        {
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                _objectCache.RemoveDelegate(value);
                if (_rt != JSRuntime.Null)
                {
                    JSApi.JSB_FreeValueRT(_rt, value);
                }
            }
            else
            {
                var act = new JSAction()
                {
                    value = value,
                    callback = _FreeValueAndDelegationAction,
                };

                lock (_pendingActions)
                {
                    if (!EnqueuePendingAction(act))
                    {
                        _logger?.Write(LogLevel.Error, "[DebugInfo] {0}", debugInfo);
                    }
                }
            }
            return true;
        }

        // 可在 GC 线程直接调用此方法
        public void FreeScriptValue(JSValue value)
        {
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                _objectCache.RemoveScriptValue(value);
                if (_rt != JSRuntime.Null)
                {
                    JSApi.JSB_FreeValueRT(_rt, value);
                }
            }
            else
            {
                var act = new JSAction()
                {
                    value = value,
                    callback = _FreeValueAndScriptValueAction,
                };

                lock (_pendingActions)
                {
                    EnqueuePendingAction(act);
                }
            }
        }

        // 可在 GC 线程直接调用此方法
        public void FreeScriptPromise(JSValue value)
        {
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                _objectCache.RemoveScriptPromise(value);
                if (_rt != JSRuntime.Null)
                {
                    JSApi.JSB_FreeValueRT(_rt, value);
                }
            }
            else
            {
                var act = new JSAction()
                {
                    value = value,
                    callback = _FreeValueAndScriptPromiseAction,
                };
                lock (_pendingActions)
                {
                    EnqueuePendingAction(act);
                }
            }
        }

        // 可在 GC 线程直接调用此方法
        public void FreeValue(JSValue value)
        {
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                if (_rt != JSRuntime.Null)
                {
                    JSApi.JSB_FreeValueRT(_rt, value);
                }
            }
            else
            {
                var act = new JSAction()
                {
                    value = value,
                    callback = _FreeValueAction,
                };
                lock (_pendingActions)
                {
                    EnqueuePendingAction(act);
                }
            }
        }

        // 可在 GC 线程直接调用此方法
        public void FreeValues(JSValue[] values)
        {
            if (values == null)
            {
                return;
            }
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                for (int i = 0, len = values.Length; i < len; i++)
                {
                    JSApi.JSB_FreeValueRT(_rt, values[i]);
                }
            }
            else
            {
                lock (_pendingActions)
                {
                    for (int i = 0, len = values.Length; i < len; i++)
                    {
                        var act = new JSAction()
                        {
                            value = values[i],
                            callback = _FreeValueAction,
                        };
                        EnqueuePendingAction(act);
                    }
                }
            }
        }

        // 可在 GC 线程直接调用此方法
        public unsafe void FreeValues(int argc, JSValue* values)
        {
            if (argc == 0)
            {
                return;
            }
            if (_mainThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                for (int i = 0; i < argc; i++)
                {
                    JSApi.JSB_FreeValueRT(_rt, values[i]);
                }
            }
            else
            {
                lock (_pendingActions)
                {
                    for (int i = 0; i < argc; i++)
                    {
                        var act = new JSAction()
                        {
                            value = values[i],
                            callback = _FreeValueAction,
                        };
                        EnqueuePendingAction(act);
                    }
                }
            }
        }

        public bool EnqueueAction(JSActionCallback callback, object args)
        {
            lock (_pendingActions)
            {
                EnqueuePendingAction(new JSAction() { callback = callback, args = args });
            }
            return true;
        }

        public bool EnqueueAction(JSAction action)
        {
            lock (_pendingActions)
            {
                EnqueuePendingAction(action);
            }
            return true;
        }

        /// <summary>
        /// try to eval the main module
        /// </summary>
        public void EvalMain(string fileName)
        {
            ResolveModule(fileName, true);
            OnMainModuleLoaded?.Invoke(this);
        }

        public void EvalFile(string fileName)
        {
            EvalFile(fileName, typeof(void));
        }

        public T EvalFile<T>(string fileName)
        {
            return (T)EvalFile(fileName, typeof(T));
        }

        public object EvalFile(string fileName, Type returnType)
        {
            var resolvedPath = ResolveFilePath("", fileName);
            if (resolvedPath != null)
            {
                var source = _fileSystem.ReadAllBytes(resolvedPath);
                return _mainContext.EvalSource(source, resolvedPath, returnType);
            }
            else
            {
                throw new Exception("can not resolve file path");
            }
        }

        public bool IsMainThread()
        {
            return _mainThreadId == Thread.CurrentThread.ManagedThreadId;
        }

        // main loop
        public void Update(int ms)
        {
            if (!_isValid || !_isRunning)
            {
                return;
            }

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isCompiling)
            {
                ScriptEngine.Shutdown();
                _logger?.Write(LogLevel.Warn, "assembly reloading, shutdown script engine immediately");
                return;
            }
#endif
            if (_pendingActions.Count != 0)
            {
                ExecutePendingActions();
            }

            OnUpdate?.Invoke(); //TODO: optimize
            ExecutePendingJob();

            // poll here;
            _timerManager.Update(ms);
            _autorelease.Drain();
        }

        public void ExecutePendingJob()
        {
            JSContext ctx;
            while (true)
            {
                var err = JSApi.JS_ExecutePendingJob(_rt, out ctx);
                if (err >= 0)
                {
                    break;
                }

                if (err < 0)
                {
                    ctx.print_exception();
                }
            }
        }

        private void ExecutePendingActions()
        {
            lock (_pendingActions)
            {
                while (true)
                {
                    if (_pendingActions.Count == 0)
                    {
                        break;
                    }

                    var action = _pendingActions.Dequeue();
                    try
                    {
                        action.callback(this, action);
                    }
                    catch (Exception exception)
                    {
                        _logger?.WriteException(exception);
                    }
                }
            }
        }

        ~ScriptRuntime()
        {
            Shutdown();
        }

        public void Shutdown()
        {
            //TODO: lock?
            if (!_isWorker)
            {
                Destroy();
            }
            else
            {
                _isRunning = false;
            }
        }

        public void Destroy()
        {
            lock (this)
            {
                if (!_isValid)
                {
                    return;
                }
                _isValid = false;
                try
                {
                    for (int i = 0, count = _moduleResolvers.Count; i < count; i++)
                    {
                        var resolver = _moduleResolvers[i];
                        resolver.Release();
                    }
                    OnDestroy?.Invoke(this);
                }
                catch (Exception e)
                {
                    _logger?.WriteException(e);
                }
            }

            _isInitialized = false;
            _isRunning = false;

            _timerManager.Destroy();
            _typeDB.Destroy();
#if !JSB_WITH_V8_BACKEND
            _objectCache.Destroy();
#endif
            GC.Collect();
            GC.WaitForPendingFinalizers();
            ExecutePendingActions();

            // _rwlock.EnterWriteLock();
            for (int i = 0, count = _contextRefs.Count; i < count; i++)
            {
                var contextRef = _contextRefs[i];
                contextRef.target.Destroy();
            }

            _contextRefs.Clear();
            _mainContext = null;
            // _rwlock.ExitWriteLock();


            if (_asyncManager != null)
            {
                _asyncManager.Destroy();
                _asyncManager = null;
            }

            lock (_pendingActions)
            {
                if (_pendingActions.Count > 0)
                {
                    _logger?.Write(LogLevel.Assert, "unexpected pending actions");
                }
            }

            if (JSApi.JSB_FreeRuntime(_rt) == 0)
            {
                _logger?.Write(LogLevel.Assert, "gc object leaks");
            }
            _objectCache.Destroy();
            var id = _runtimeId;
            _runtimeId = -1;
            _rt = JSRuntime.Null;

            try
            {
                OnAfterDestroy?.Invoke(id);
            }
            catch (Exception e)
            {
                _logger?.WriteException(e);
            }
        }

        public static implicit operator JSRuntime(ScriptRuntime se)
        {
            return se._rt;
        }
    }
}