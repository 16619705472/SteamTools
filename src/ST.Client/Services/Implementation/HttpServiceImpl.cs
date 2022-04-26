using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using System.Collections.Concurrent;
using System.IO;
using System.IO.FileFormats;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
//using static System.Application.ForwardHelper;
using static System.Application.Services.IHttpService;
using AsyncLock = Nito.AsyncEx.AsyncLock;

namespace System.Application.Services.Implementation
{
    public sealed class HttpServiceImpl : GeneralHttpClientFactory, IHttpService
    {
        readonly JsonSerializer jsonSerializer = new();
        //readonly Lazy<ICloudServiceClient> _csc = new(() => DI.Get<ICloudServiceClient>());

        //ICloudServiceClient Csc => _csc.Value;

        JsonSerializer IHttpService.Serializer => jsonSerializer;

        IHttpClientFactory IHttpService.Factory => _clientFactory;

        IHttpPlatformHelperService IHttpService.PlatformHelper => http_helper;

        public HttpServiceImpl(
            ILoggerFactory loggerFactory,
            IHttpPlatformHelperService http_helper,
            IHttpClientFactory clientFactory)
            : base(loggerFactory.CreateLogger(TAG), http_helper, clientFactory)
        {
        }

        async Task<T?> SendAsync<T>(
            bool isCheckHttpUrl,
            string? requestUri,
            Func<HttpRequestMessage> requestFactory,
            string? accept,
            //bool enableForward,
            CancellationToken cancellationToken,
            Action<HttpResponseMessage>? handlerResponse = null,
            Action<HttpResponseMessage>? handlerResponseByIsNotSuccessStatusCode = null,
            string? clientName = null) where T : notnull
        {
            HttpRequestMessage? request = null;
            bool requestIsSend = false;
            HttpResponseMessage? response = null;
            bool notDisposeResponse = false;
            try
            {
                request = requestFactory();
                requestUri ??= request.RequestUri?.ToString();

                if (!isCheckHttpUrl && !Browser2.IsHttpUrl(requestUri)) return default;

                //if (enableForward && IsAllowUrl(requestUri))
                //{
                //    try
                //    {
                //        requestIsSend = true;
                //        response = await Csc.Forward(request,
                //            HttpCompletionOption.ResponseHeadersRead,
                //            cancellationToken);
                //    }
                //    catch (Exception e)
                //    {
                //        logger.LogWarning(e, "CloudService Forward Fail, requestUri: {0}", requestUri);
                //        response = null;
                //    }
                //}

                if (response == null)
                {
                    var client = CreateClient(clientName);
                    if (requestIsSend)
                    {
                        request.Dispose();
                        request = requestFactory();
                    }
                    response = await client.SendAsync(request,
                      HttpCompletionOption.ResponseHeadersRead,
                      cancellationToken).ConfigureAwait(false);
                }

                handlerResponse?.Invoke(response);

                if (response.IsSuccessStatusCode)
                {
                    if (response.Content != null)
                    {
                        var rspContentClrType = typeof(T);
                        if (rspContentClrType == typeof(string))
                        {
                            return (T)(object)await response.Content.ReadAsStringAsync();
                        }
                        else if (rspContentClrType == typeof(byte[]))
                        {
                            return (T)(object)await response.Content.ReadAsByteArrayAsync();
                        }
                        else if (rspContentClrType == typeof(Stream))
                        {
                            notDisposeResponse = true;
                            return (T)(object)await response.Content.ReadAsStreamAsync();
                        }
                        var mime = response.Content.Headers.ContentType?.MediaType ?? accept;
                        switch (mime)
                        {
                            case MediaTypeNames.JSON:
                            case MediaTypeNames.XML:
                            case MediaTypeNames.XML_APP:
                                {
                                    using var stream = await response.Content
                                        .ReadAsStreamAsync().ConfigureAwait(false);
                                    using var reader = new StreamReader(stream, Encoding.UTF8);
                                    switch (mime)
                                    {
                                        case MediaTypeNames.JSON:
                                            {
                                                using var json = new JsonTextReader(reader);
                                                return jsonSerializer.Deserialize<T>(json);
                                            }
                                        case MediaTypeNames.XML:
                                        case MediaTypeNames.XML_APP:
                                            {
                                                var xmlSerializer = new XmlSerializer(typeof(T));
                                                return (T?)xmlSerializer.Deserialize(reader);
                                            }
                                    }
                                }
                                break;
                        }
                    }
                }
                else
                {
                    handlerResponseByIsNotSuccessStatusCode?.Invoke(response);
                }
            }
            catch (Exception e)
            {
                var knownType = e.GetKnownType();
                if (knownType == ExceptionKnownType.Unknown)
                {
                    logger.LogError(e, "SendAsync Fail, requestUri: {0}", requestUri);
                }
            }
            finally
            {
                request?.Dispose();
                if (!notDisposeResponse)
                {
                    response?.Dispose();
                }
            }
            return default;
        }

        public Task<T?> SendAsync<T>(
            string? requestUri,
            Func<HttpRequestMessage> requestFactory,
            string? accept,
            //bool enableForward,
            CancellationToken cancellationToken,
            Action<HttpResponseMessage>? handlerResponse = null,
            Action<HttpResponseMessage>? handlerResponseByIsNotSuccessStatusCode = null,
            string? clientName = null) where T : notnull
        {
            return SendAsync<T>(
                isCheckHttpUrl: false,
                requestUri,
                requestFactory,
                accept,
                //enableForward,
                cancellationToken,
                handlerResponse,
                handlerResponseByIsNotSuccessStatusCode,
                clientName);
        }

        public Task<T?> GetAsync<T>(
            string requestUri,
            string accept,
            CancellationToken cancellationToken,
            string? cookie = null) where T : notnull
        {
            if (!Browser2.IsHttpUrl(requestUri)) return Task.FromResult(default(T?));
            return SendAsync<T>(isCheckHttpUrl: true, requestUri, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (cookie != null)
                {
                    request.Headers.Add("Cookie", cookie);
                }
                request.Headers.Accept.ParseAdd(accept);
                request.Headers.UserAgent.ParseAdd(http_helper.UserAgent);
                return request;
            }, accept/*, true*/, cancellationToken);
        }

        async Task<string?> GetImageAsync_(
            string requestUri,
            string localCacheFilePath,
            CancellationToken cancellationToken)
        {
            var localCacheFilePathExists = File.Exists(localCacheFilePath);

            try
            {
                if (localCacheFilePathExists) // 存在缓存文件
                {
                    using var fileStream = IOPath.OpenRead(localCacheFilePath);
                    if (fileStream == null)
                        return null;
                    if (FileFormat.IsImage(fileStream, out var format))
                    {
                        if (http_helper.SupportedImageFormats.Contains(format)) // 读取缓存并且格式符合要求
                        {
                            fileStream.Close();
                            //logger.LogDebug("GetImageAsync localCacheFilePath: {0}", localCacheFilePath);
                            return localCacheFilePath;
                        }
                        else // 格式不准确
                        {
                            logger.LogWarning("GetImageAsync Not Supported ImageFormat: {0}.", format);
                        }
                    }
                    else // 未知的图片格式
                    {
                        logger.LogWarning("GetImageAsync Unknown ImageFormat.");
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "GetImageAsync_ Load LocalFile Fail.");
            }

            if (localCacheFilePathExists)
            {
                File.Delete(localCacheFilePath); // 必须删除文件，重新下载覆盖
            }

            try
            {
                //logger.LogDebug("GetImageAsync requestUri: {0}", requestUri);
                //#if DEBUG
                //                var now = DateTime.Now;
                //                var filePath = Path.Combine(IOPath.CacheDirectory, $"{now.ToString(DateTimeFormat.Connect)}-{Hashs.String.Crc32(requestUri)}");
                //                File.WriteAllText(filePath, now.ToString());
                //#endif
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd(http_helper.AcceptImages);
                request.Headers.UserAgent.ParseAdd(http_helper.UserAgent);
                var client = CreateClient();
                var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var localCacheFilePath2 = localCacheFilePath + ".download-cache";
                    var fileStream = File.Create(localCacheFilePath2);
                    cancellationToken.Register(() =>
                    {
                        fileStream.Close();
                        IOPath.FileTryDelete(localCacheFilePath2);
                    });
                    await stream.CopyToAsync(fileStream, cancellationToken);
                    await fileStream.FlushAsync(cancellationToken);
                    bool isSupportedImageFormat;
                    if (FileFormat.IsImage(fileStream, out var format))
                    {
                        if (http_helper.SupportedImageFormats.Contains(format)) // 读取缓存并且格式符合要求
                        {
                            isSupportedImageFormat = true;
                        }
                        else // 格式不准确
                        {
                            logger.LogWarning("GetImageAsync Download Not Supported ImageFormat: {0}.", format);
                            isSupportedImageFormat = false;
                        }
                    }
                    else // 未知的图片格式
                    {
                        logger.LogWarning("GetImageAsync Download Unknown ImageFormat.");
                        isSupportedImageFormat = false;
                    }
                    fileStream.Close();
                    if (isSupportedImageFormat)
                    {
                        File.Move(localCacheFilePath2, localCacheFilePath);
                        return localCacheFilePath;
                    }
                    else
                    {
                        IOPath.FileTryDelete(localCacheFilePath2);
                    }
                }
            }
            catch (Exception e)
            {
#if !DEBUG
                if (e is Net.Sockets.SocketException se && se.SocketErrorCode == Net.Sockets.SocketError.ConnectionReset)
                {
                    // https://docs.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2
                    // 10054 WSAECONNRESET
                    return default;
                }
#endif
                logger.LogWarning(e, "GetImageAsync_ Fail.");
            }

            return default;
        }

        readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Task<string?>>> get_image_pipeline = new();

        public async Task<Stream?> GetImageStreamAsync(string requestUri, string channelType, CancellationToken cancellationToken)
        {
            var file = await GetImageLocalFilePathByPollyAsync(requestUri, channelType, cancellationToken);
            return string.IsNullOrEmpty(file) ? null : File.OpenRead(file);
        }

        #region Polly

        const int numRetries = 5;

        static TimeSpan PollyRetryAttempt(int attemptNumber)
        {
            var powY = attemptNumber % numRetries;
            var timeSpan = TimeSpan.FromMilliseconds(Math.Pow(2, powY));
            int addS = attemptNumber / numRetries;
            if (addS > 0) timeSpan = timeSpan.Add(TimeSpan.FromSeconds(addS));
            return timeSpan;
        }

        #endregion

        Task<string?> IHttpService.GetImageAsync(string requestUri, string channelType, CancellationToken cancellationToken)
        {
            return GetImageLocalFilePathByPollyAsync(requestUri, channelType, cancellationToken);
        }

        public async Task<string?> GetImageLocalFilePathByPollyAsync(string requestUri, string channelType, CancellationToken cancellationToken)
        {
            var r = await Policy.HandleResult<string?>(string.IsNullOrWhiteSpace)
              .WaitAndRetryAsync(numRetries, PollyRetryAttempt)
              .ExecuteAsync(ct => GetImageLocalFilePathAsync(requestUri, channelType, ct), cancellationToken);
            return r;
        }

        readonly AsyncLock lockGetImageLocalFilePathAsync = new();

        public async Task<string?> GetImageLocalFilePathAsync(string requestUri, string channelType, CancellationToken cancellationToken)
        {
            if (!Browser2.IsHttpUrl(requestUri)) return null;

            Task<string?> task;
            ConcurrentDictionary<string, Task<string?>>? pairs = null;

            using (await lockGetImageLocalFilePathAsync.LockAsync(cancellationToken))
            {
                if (get_image_pipeline.ContainsKey(channelType))
                {
                    pairs = get_image_pipeline[channelType];
                    if (pairs.ContainsKey(requestUri))
                    {
                        var findResult = await pairs[requestUri];
                        return findResult;
                    }
                }
                else
                {
                    get_image_pipeline.TryAdd(channelType, new ConcurrentDictionary<string, Task<string?>>());
                }

                var dirPath = GetImagesCacheDirectory(channelType);
                var fileName = Hashs.String.SHA256(requestUri) + FileEx.ImageSource;
                var localCacheFilePath = Path.Combine(dirPath, fileName);

                pairs ??= get_image_pipeline[channelType];
                task = GetImageAsync_(requestUri, localCacheFilePath, cancellationToken);
                pairs.TryAdd(requestUri, task);
            }

            var result = await task;
            pairs.TryRemove(requestUri, out var _);
            return result;
        }

        public async Task<Stream?> GetImageStreamAsync(string requestUri, CancellationToken cancellationToken)
        {
            if (!Browser2.IsHttpUrl(requestUri)) return null;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd(http_helper.AcceptImages);
                request.Headers.UserAgent.ParseAdd(http_helper.UserAgent);
                var client = CreateClient();
                var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var imageStream = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var ms = new MemoryStream(imageStream);
                    return ms;
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "GetImageStreamAsync Fail.");
            }
            return default;
        }
    }
}