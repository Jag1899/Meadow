using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Meadow.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Meadow.Core.EthTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Dynamic;
using System.Reflection;
using System.Linq.Expressions;
using System.Threading;
using Meadow.Core;
using Meadow.JsonRpc.Types;
using Meadow.JsonRpc.JsonConverters;
using System.Collections.Concurrent;
using Meadow.JsonRpc.Types.Debugging;

namespace Meadow.JsonRpc.Client
{

    interface ITaskCompletionSource
    {
        void SetResult(object result);
        void SetException(Exception ex);
        Task Task { get; }
    }

    class TaskCompletionSourceWrapper<T> : TaskCompletionSource<T>, ITaskCompletionSource
    {
        Task ITaskCompletionSource.Task => Task;

        void ITaskCompletionSource.SetException(Exception ex)
        {
            SetException(ex);
        }

        void ITaskCompletionSource.SetResult(object result)
        {
            SetResult((T)result);
        }
    }

    public delegate Task<Exception> JsonRpcErrorFormatterDelegate(IJsonRpcClient client, JsonRpcError rpcError);

    public interface IJsonRpcClient : IRpcControllerMinimal, IRpcController
    {
        Task<(JsonRpcError Error, byte[] Result)> TryCall(CallParams callParams, DefaultBlockParameter blockParameter);
        Task<(JsonRpcError Error, Hash Result)> TrySendTransaction(TransactionParams transactionParams);

        /// <summary>
        /// If true, all transactions hashes are queried for their receipt, and an exception
        /// is thrown for receipts with an unsuccessful status code.
        /// </summary>
        bool CheckBadTransactionStatus { get; set; }

        JsonRpcErrorFormatterDelegate ErrorFormatter { get; }

        /// <summary>
        /// If set, all SendTransaction calls will invoke this delegate and inssue a sendRawTransaction RPC call.
        /// </summary>
        RawTransactionSignerDelegate RawTransactionSigner { get; set; }

        /// <summary>
        /// If set then <see cref="IRpcControllerMinimal.GetTransactionReceipt(Hash)"/> will be repeated at this interval until
        /// a result is returned.
        /// </summary>
        TimeSpan TransactionReceiptPollInterval { get; set; }
    }

    /*
    public class ExceptionHandlingInterceptor : AsyncInterceptorBase
    {
        protected override async Task InterceptAsync(IInvocation invocation, Func<IInvocation, Task> proceed)
        {
            try
            {
                // Cannot simply return the the task, as any exceptions would not be caught below.
                await proceed(invocation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        protected override async Task<T> InterceptAsync<T>(IInvocation invocation, Func<IInvocation, Task<T>> proceed)
        {
            try
            {
                // Cannot simply return the the task, as any exceptions would not be caught below.
                return await proceed(invocation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw;
            }
        }
    }
    */

    public delegate Task<byte[]> RawTransactionSignerDelegate(IJsonRpcClient rpcClient, TransactionParams transactionParams);

    public class JsonRpcClient : DynamicObject, IRpcControllerMinimal
    {
        readonly Uri _serverUri;

        long _lastRequestID = 1;

        long _defaultGasLimit;
        long _defaultGasPrice;
        IJsonRpcClient _thisInterface;

        /// <summary>
        /// If true, all transactions hashes are queried for their receipt, and an exception
        /// is thrown for receipts with an unsuccessful status code.
        /// </summary>
        bool CheckBadTransactionStatus { get; set; } = true;

        public JsonRpcErrorFormatterDelegate ErrorFormatter { get; }

        public RawTransactionSignerDelegate RawTransactionSigner { get; set; }

        public TimeSpan TransactionReceiptPollInterval { get; set; }

        public static IJsonRpcClient Create(Uri serverUri, long defaultGasLimit, long defaultGasPrice, JsonRpcErrorFormatterDelegate errorFormatter = null)
        {
            var dynamicClient = new JsonRpcClient(serverUri, errorFormatter);
            dynamicClient._defaultGasLimit = defaultGasLimit;
            dynamicClient._defaultGasPrice = defaultGasPrice;


            // TODO: test
            // TODO: check out https://github.com/mtamme/NProxy
            //       http://naeem.khedarun.co.uk/blog/2016/01/18/a-look-at-performance-on-dotnet-dynamic-proxies-1448894394346/
            /*
            var generator = new ProxyGenerator();
            var interceptor = new ExceptionHandlingInterceptor();
            var proxyGenOptions = new ProxyGenerationOptions();
            proxyGenOptions.AddMixinInstance(dynamicClient);
            var proxyInst = generator.CreateInterfaceProxyWithoutTarget<IJsonRpcClient>(proxyGenOptions, interceptor.ToInterceptor());
            */

            IJsonRpcClient clientInterface = ImpromptuInterface.Impromptu.ActLike<IJsonRpcClient>(dynamicClient);
            dynamicClient._thisInterface = clientInterface;
            return clientInterface;
        }

        private JsonRpcClient(Uri serverUri, JsonRpcErrorFormatterDelegate errorFormatter)
        {
            _serverUri = serverUri;

            ErrorFormatter = errorFormatter;
        }

        class ReflectedMethodInfo
        {
            public MethodInfo MethodInfo;
            public RpcApiMethod RpcMethod;
            public Type TaskGenericArgType;

            public void Deconstruct(out MethodInfo methodInfo, out RpcApiMethod rpcMethod, out Type taskGenericArgType)
            {
                methodInfo = MethodInfo;
                rpcMethod = RpcMethod;
                taskGenericArgType = TaskGenericArgType;
            }
        }

        static ConcurrentDictionary<(string MethodName, int ArgCount), ReflectedMethodInfo> _interfaceMethodCache = new ConcurrentDictionary<(string, int), ReflectedMethodInfo>();

        ReflectedMethodInfo GetReflectedMethodInfo(string methodName, object[] args)
        {
            // Search RPC Interface for method that matches the provided name and arguments.
            var matchingMethods = typeof(IRpcController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name.Equals(methodName, StringComparison.Ordinal))
                .Select(m => (Member: m, Params: m.GetParameters()))
                .Where(m => ParamMatch(m.Params, args))
                .ToArray();

            if (matchingMethods.Length == 0)
            {
                throw new Exception($"Could not find method in Rpc interface. Name: {methodName}, Args: {string.Join(", ", args.Select(a => a.ToString()))}");
            }
            else if (matchingMethods.Length > 1)
            {
                throw new Exception($"Multiple matching methods in Rpc interface. Name: {methodName}, Args: {string.Join(", ", args.Select(a => a.ToString()))}");
            }

            (MethodInfo rpcInterfaceMethod, _) = matchingMethods[0];

            // Get the rpc method string name from the interface method attribute.
            var rpcMethod = rpcInterfaceMethod
                .GetCustomAttribute<RpcApiMethodAttribute>(inherit: true)
                .Method;


            // The type of T in Task<T>
            Type taskGenericArgType = rpcInterfaceMethod.ReturnType.IsGenericType ?
                rpcInterfaceMethod.ReturnType.GetGenericArguments()[0] : typeof(object);

            var info = new ReflectedMethodInfo
            {
                MethodInfo = rpcInterfaceMethod,
                RpcMethod = rpcMethod,
                TaskGenericArgType = taskGenericArgType
            };

            return info;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            string methodName = binder.Name;

            var reflectedMethodInfo = _interfaceMethodCache.GetOrAdd(
                (methodName, args.Length),
                _ => GetReflectedMethodInfo(methodName, args));

            var (methodInfo, rpcMethod, taskGenericArgType) = reflectedMethodInfo;

            // Create the json request object.
            string jsonStr = CreateRequestObject(rpcMethod, args);

            // Peform the http json-rpc request
            Task<object> resultTask = InvokeRpcMethod(jsonStr, taskGenericArgType);

            // Convert the Task<Object> to the Task<T> of the return type expected by the interface's method.
            Task convertedTask = ConvertTask(resultTask, taskGenericArgType);
            result = convertedTask;

            return true;
        }


        /// <param name="msgJson">Full message data as json string to POST.</param>
        /// <param name="taskGenericArgType">The type to convert the json into.</param>
        async Task<object> InvokeRpcMethod(string msgJson, Type taskGenericArgType)
        {
            (JsonRpcError error, JToken result) = await CreateJsonRpcHttpRequest(msgJson);
            if (error != null)
            {
                throw error.ToException();
            }

            var resultObj = result.ToObject(taskGenericArgType, JsonRpcSerializer.Serializer);
            return resultObj;
        }

        async Task<(JsonRpcError Error, JToken Result)> InvokeRpcMethod(string jsonStr, bool throwOnError = true)
        {
            var (error, result) = await CreateJsonRpcHttpRequest(jsonStr);
            if (error != null && throwOnError)
            {
                throw error.ToException();
            }

            return (error, result);
        }

        static ConcurrentDictionary<Type, Type> _tcsTypeCache = new ConcurrentDictionary<Type, Type>();

        /// <summary>
        /// Converts a Task&lt;object&gt; to a Task&lt;T&gt; when the T type is only known at runtime.
        /// </summary>
        /// <param name="objectTask"></param>
        /// <param name="desiredTaskType">The type to use when creating the Task&lt;T&gt;.</param>
        /// <returns></returns>
        static Task ConvertTask(Task<object> objectTask, Type desiredTaskType)
        {
            // Create TaskCompletionSource<T> where the generic type matches the expected Task<T> return type.
            Type tcsType = _tcsTypeCache.GetOrAdd(desiredTaskType, t => typeof(TaskCompletionSourceWrapper<>).MakeGenericType(desiredTaskType));
            var taskCompletionSource = (ITaskCompletionSource)Activator.CreateInstance(tcsType);

            // We need to convert the Task<object> to the Task<T> return type expected by this interface method's return type.
            // Use a callback instead of async/await since we are forced into this sync by method override.
            objectTask.ContinueWith((Task<object> jTokenTask) =>
            {
                if (jTokenTask.Status == TaskStatus.Faulted)
                {
                    taskCompletionSource.SetException(jTokenTask.Exception.InnerException);
                }
                else if (jTokenTask.Status == TaskStatus.RanToCompletion)
                {
                    object resultObj = jTokenTask.Result;
                    taskCompletionSource.SetResult(resultObj);
                }
                else
                {
                    taskCompletionSource.SetException(new Exception("Unexpected task status " + jTokenTask.Status));
                }
            });

            return taskCompletionSource.Task;
        }

        static bool ParamMatch(ParameterInfo[] paramInfo, object[] args)
        {
            if (paramInfo.Length != args.Length)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                var paramType = paramInfo[i].ParameterType;
                var argType = args[i].GetType();
                if (!paramType.IsAssignableFrom(argType))
                {
                    return false;
                }
            }

            return true;
        }

        string CreateRequestObject(RpcApiMethod rpcMethod, params object[] args)
        {
            var jArgs = JArray.FromObject(args, JsonRpcSerializer.Serializer);
            var jObj = new JObject();
            jObj["jsonrpc"] = "2.0";
            jObj["method"] = rpcMethod.Value();
            jObj["id"] = Interlocked.Increment(ref _lastRequestID);
            jObj["params"] = jArgs;
            var jsonStr = jObj.ToString();
            return jsonStr;
        }

        async Task<(JsonRpcError Error, JToken Result)> CreateJsonRpcHttpRequest(string msgJson)
        {
            var payload = new StringContent(msgJson, Encoding.UTF8, "application/json");
            var requestMsg = new HttpRequestMessage(HttpMethod.Post, _serverUri)
            {
                Content = payload
            };

            var response = await RootServiceProvider.GetRpcHttpClient().SendAsync(requestMsg);

            var responseBody = await response.Content.ReadAsStringAsync();

            JObject jObj;
            try
            {
                jObj = JObject.Parse(responseBody);
            }
            catch
            {
                response.EnsureSuccessStatusCode();
                throw;
            }

            if (jObj.TryGetValue("error", out var errorToken))
            {
                var jsonRpcError = errorToken.ToObject<JsonRpcError>();
                return (jsonRpcError, null);
            }
            else if (jObj.TryGetValue("result", out var resultToken))
            {
                return (null, resultToken);
            }
            else
            {
                throw new Exception("Unexpected JSON-RPC response: " + responseBody);
            }
        }


        public async Task<Hash> SendTransaction(TransactionParams transactionParams)
        {
            transactionParams.Gas = transactionParams.Gas ?? _defaultGasLimit;
            transactionParams.GasPrice = transactionParams.GasPrice ?? _defaultGasPrice;

            string request;
            if (RawTransactionSigner != null)
            {
                var signed = await RawTransactionSigner(_thisInterface, transactionParams);
                request = CreateRequestObject(RpcApiMethod.eth_sendRawTransaction, signed);
            }
            else
            {
                request = CreateRequestObject(RpcApiMethod.eth_sendTransaction, transactionParams);
            }

            var (error, result) = await InvokeRpcMethod(request, throwOnError: false);
            if (error != null)
            {
                throw error.ToException();
            }

            var hashHexStr = result.Value<string>();
            var hash = HexConverter.HexToValue<Hash>(hashHexStr);
            return hash;
        }

        public async Task<(JsonRpcError Error, Hash Result)> TrySendTransaction(TransactionParams transactionParams)
        {
            transactionParams.Gas = transactionParams.Gas ?? _defaultGasLimit;
            transactionParams.GasPrice = transactionParams.GasPrice ?? _defaultGasPrice;

            string request;
            if (RawTransactionSigner != null)
            {
                var signed = await RawTransactionSigner(_thisInterface, transactionParams);
                request = CreateRequestObject(RpcApiMethod.eth_sendRawTransaction, signed);
            }
            else
            {
                request = CreateRequestObject(RpcApiMethod.eth_sendTransaction, transactionParams);
            }

            var (error, result) = await InvokeRpcMethod(request, throwOnError: false);
            if (error != null)
            {
                return (error, default);
            }

            var hashHexStr = result.Value<string>();
            var hash = HexConverter.HexToValue<Hash>(hashHexStr);
            return (null, hash);
        }

        public async Task<byte[]> Call(CallParams callParams, DefaultBlockParameter blockParameter)
        {
            callParams.Gas = callParams.Gas ?? _defaultGasLimit;
            callParams.GasPrice = callParams.GasPrice ?? _defaultGasPrice;
            blockParameter = blockParameter ?? DefaultBlockParameter.Default;

            var request = CreateRequestObject(RpcApiMethod.eth_call, callParams, blockParameter);

            var (error, result) = await InvokeRpcMethod(request);
            return result.Value<string>().HexToBytes();
        }

        public async Task<(JsonRpcError Error, byte[] Result)> TryCall(CallParams callParams, DefaultBlockParameter blockParameter)
        {
            callParams.Gas = callParams.Gas ?? _defaultGasLimit;
            callParams.GasPrice = callParams.GasPrice ?? _defaultGasPrice;
            blockParameter = blockParameter ?? DefaultBlockParameter.Default;

            var request = CreateRequestObject(RpcApiMethod.eth_call, callParams, blockParameter);

            var (error, result) = await InvokeRpcMethod(request, throwOnError: false);
            if (error != null)
            {
                return (error, default);
            }

            var bytes = result.Value<string>().HexToBytes();
            return (null, bytes);
        }

        public async Task<UInt256> EstimateGas(CallParams callParams, DefaultBlockParameter blockParameter = null)
        {
            callParams.Gas = callParams.Gas ?? _defaultGasLimit;
            callParams.GasPrice = callParams.GasPrice ?? _defaultGasPrice;
            blockParameter = blockParameter ?? DefaultBlockParameter.Default;

            var request = CreateRequestObject(RpcApiMethod.eth_estimateGas, callParams, blockParameter);

            var (error, result) = await InvokeRpcMethod(request);
            var uintHexStr = result.Value<string>();
            return HexConverter.HexToInteger<UInt256>(uintHexStr);
        }

        public async Task<TransactionReceipt> GetTransactionReceipt(Hash transactionHash)
        {
            while (true)
            {
                var requestData = CreateRequestObject(RpcApiMethod.eth_getTransactionReceipt, transactionHash);
                var (error, result) = await InvokeRpcMethod(requestData);
                var receipt = result.ToObject<TransactionReceipt>();

                if (receipt == null && TransactionReceiptPollInterval.Ticks > 0)
                {
                    await Task.Delay(TransactionReceiptPollInterval);
                }
                else
                {
                    return receipt;
                }
            }
        }

        public async Task<Address[]> Accounts()
        {
            var requestData = CreateRequestObject(RpcApiMethod.eth_accounts);
            var (error, result) = await InvokeRpcMethod(requestData);
            var receipt = result.ToObject<Address[]>(JsonRpcSerializer.Serializer);
            return receipt;
        }

        public async Task<UInt256> GasPrice()
        {
            var requestData = CreateRequestObject(RpcApiMethod.eth_gasPrice);
            var (error, result) = await InvokeRpcMethod(requestData);
            var uintHexStr = result.Value<string>();
            return HexConverter.HexToInteger<UInt256>(uintHexStr);
        }

        public async Task SetCoverageEnabled(bool enabled)
        {
            // Call our enable coverage command
            var requestData = CreateRequestObject(RpcApiMethod.testing_setCoverageEnabled, enabled);
            var (error, result) = await InvokeRpcMethod(requestData);
        }

        public async Task SetTracingEnabled(bool enabled)
        {
            // Set our enable tracing command
            var requestData = CreateRequestObject(RpcApiMethod.testing_setTracingEnabled, enabled);
            var (error, result) = await InvokeRpcMethod(requestData);
        }

        public async Task<ExecutionTrace> GetExecutionTrace()
        {
            // Set our enable tracing command
            var requestData = CreateRequestObject(RpcApiMethod.testing_getExecutionTrace);
            var (error, result) = await InvokeRpcMethod(requestData);
            var executionTrace = result.ToObject<ExecutionTrace>(JsonRpcSerializer.Serializer);
            return executionTrace;
        }

        public async Task<CompoundCoverageMap[]> GetAllCoverageMaps()
        {
            // Obtain all of our coverage maps.
            var requestData = CreateRequestObject(RpcApiMethod.testing_getAllCoverageMaps);
            var (error, result) = await InvokeRpcMethod(requestData);
            var coverageMaps = result.ToObject<CompoundCoverageMap[]>(JsonRpcSerializer.Serializer);
            return coverageMaps;
        }

        public async Task ClearCoverage()
        {
            // Call our clear coverage command
            var requestData = CreateRequestObject(RpcApiMethod.testing_clearAllCoverage);
            var (error, result) = await InvokeRpcMethod(requestData);
        }
    }
}