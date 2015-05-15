using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinqToTwitter.Net;
using Windows.Web.Http;

namespace LinqToTwitter.WindowsStore
{
    /// <summary>
    /// Logic that performs actual communication with Twitter
    /// </summary>
    public partial class TwitterExecute : ITwitterExecute, IDisposable
    {
        internal const string DefaultUserAgent = "LINQ-To-Twitter/3.1";
        internal const int DefaultReadWriteTimeout = 300000;
        internal const int DefaultTimeout = 100000;

        /// <summary>
        /// Gets or sets the object that can send authorized requests to Twitter.
        /// </summary>
        public IAuthorizer Authorizer { get; set; }

        /// <summary>
        /// Timeout (milliseconds) for writing to request 
        /// stream or reading from response stream
        /// </summary>
        public int ReadWriteTimeout { get; set; }

        /// <summary>
        /// Timeout (milliseconds) to wait for a server response
        /// </summary>
        public int Timeout { get; set; }

        /// <summary>
        /// Gets the most recent URL executed
        /// </summary>
        /// <remarks>
        /// This is very useful for debugging
        /// </remarks>
        public Uri LastUrl { get; private set; }

        /// <summary>
        /// list of response headers from query
        /// </summary>
        public IDictionary<string, string> ResponseHeaders { get; set; }

        /// <summary>
        /// Gets and sets HTTP UserAgent header
        /// </summary>
        public string UserAgent
        {
            get
            {
                return Authorizer.UserAgent;
            }
            set
            {
                Authorizer.UserAgent =
                    string.IsNullOrWhiteSpace(value) ?
                        Authorizer.UserAgent :
                        value + ", " + Authorizer.UserAgent;
            }
        }

        /// <summary>
        /// Assign your TextWriter instance to receive LINQ to Twitter output
        /// </summary>
        public static TextWriter Log { get; set; }

        readonly object streamingCallbackLock = new object();

        /// <summary>
        /// Allows users to process content returned from stream
        /// </summary>
        public Func<StreamContent, Task> StreamingCallbackAsync { get; set; }

        /// <summary>
        /// HttpClient instance being used in a streaming operation
        /// </summary>
        internal HttpClient StreamingClient { get; set; }

        /// <summary>
        /// Set to true to close stream, false means stream is still open
        /// </summary>
        public bool IsStreamClosed { get; internal set; }

        /// <summary>
        /// Allows callers to cancel operation (where applicable)
        /// </summary>
        public CancellationToken CancellationToken { get; set; }

        readonly object asyncCallbackLock = new object();

        /// <summary>
        /// supports testing
        /// </summary>
        public TwitterExecute(IAuthorizer authorizer)
        {
            if (authorizer == null)
            {
                throw new ArgumentNullException("authorizedClient");
            }

            Authorizer = authorizer;
            Authorizer.UserAgent = Authorizer.UserAgent ?? DefaultUserAgent;
        }

        /// <summary>
        /// Used in queries to read information from Twitter API endpoints.
        /// </summary>
        /// <param name="request">Request with url endpoint and all query parameters</param>
        /// <param name="reqProc">Request Processor for Async Results</param>
        /// <returns>XML Respose from Twitter</returns>
        public async Task<string> QueryTwitterAsync<T>(Request request, IRequestProcessor<T> reqProc)
        {
            WriteLog(request.FullUrl, "QueryTwitterAsync");

            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(request.FullUrl));

            var parms = request.RequestParameters
                               .ToDictionary(
                                    key => key.Name,
                                    val => val.Value);
            //var handler = new GetMessageHandler(this, parms, request.FullUrl);

            using (var client = new HttpClient())
            {
                //if (Timeout != 0)
                //    client.Timeout = new TimeSpan(0, 0, 0, Timeout);

                var msg = await client.SendRequestAsync(req); //.ConfigureAwait(false);

                return await HandleResponseAsync(msg).ConfigureAwait(false);
            }
        }

        private System.Net.Http.HttpMethod ConvertToSystemNetWebHttpMethod(HttpMethod method) {

            if (method == Windows.Web.Http.HttpMethod.Delete) return System.Net.Http.HttpMethod.Delete;
            else if (method == Windows.Web.Http.HttpMethod.Get) return System.Net.Http.HttpMethod.Get;
            else if (method == Windows.Web.Http.HttpMethod.Head) return System.Net.Http.HttpMethod.Head;
            else if (method == Windows.Web.Http.HttpMethod.Options) return System.Net.Http.HttpMethod.Options;
            else if (method == Windows.Web.Http.HttpMethod.Post) return System.Net.Http.HttpMethod.Post;
            else if (method == Windows.Web.Http.HttpMethod.Put) return System.Net.Http.HttpMethod.Put;


            return null;


        }

        internal void SetAuthorizationHeader(HttpMethod method, string url, IDictionary<string, string> parms, HttpRequestMessage req)
        {
            var authStringParms = parms.ToDictionary(parm => parm.Key, elm => elm.Value);
            authStringParms.Add("oauth_consumer_key", Authorizer.CredentialStore.ConsumerKey);
            authStringParms.Add("oauth_token", Authorizer.CredentialStore.OAuthToken);

            string authorizationString = Authorizer.GetAuthorizationString(ConvertToSystemNetWebHttpMethod(method), url, authStringParms);

            req.Headers.Add("Authorization", authorizationString);
        }

        internal void SetAuthorizationHeader(HttpMethod method, string url, IDictionary<string, string> parms, HttpClient client)
        {
            var authStringParms = parms.ToDictionary(parm => parm.Key, elm => elm.Value);
            authStringParms.Add("oauth_consumer_key", Authorizer.CredentialStore.ConsumerKey);
            authStringParms.Add("oauth_token", Authorizer.CredentialStore.OAuthToken);

            string authorizationString = Authorizer.GetAuthorizationString(ConvertToSystemNetWebHttpMethod(method), url, authStringParms);

            client.DefaultRequestHeaders.Add("Authorization", authorizationString);
        }


        /// <summary>
        /// Performs a query on the Twitter Stream.
        /// </summary>
        /// <param name="request">Request with url endpoint and all query parameters.</param>
        /// <returns>
        /// Caller expects an JSON formatted string response, but
        /// real response(s) with streams is fed to the callback.
        /// </returns>
        public async Task<string> QueryTwitterStreamAsync(Request request)
        {
            WriteLog(request.FullUrl, "QueryTwitterStreamAsync");

            var handler = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
            //if (Authorizer.Proxy != null && handler.SupportsProxy)
            //    handler.Proxy = Authorizer.Proxy;

            using (StreamingClient = new HttpClient(handler))
            {
                //StreamingClient.Timeout = TimeSpan.FromMilliseconds(System.Threading.Timeout.Infinite);

                var httpRequest = ConfigureRequest(request);

                var response = await StreamingClient.SendRequestAsync(
                    httpRequest, HttpCompletionOption.ResponseHeadersRead); //.ConfigureAwait(false);

                await ThrowIfErrorAsync(response).ConfigureAwait(false);

                Stream stream = await CreateStream(response);

                const int CarriageReturn = 0x0D;
                const int LineFeed = 0x0A;

                var memStr = new MemoryStream();
                byte[] readByte;

                while (stream.CanRead && !IsStreamClosed)
                {
                    readByte = new byte[1];
                    await stream.ReadAsync(readByte, 0, 1, CancellationToken).ConfigureAwait(false);
                    byte nextByte = readByte.SingleOrDefault();

                    CancellationToken.ThrowIfCancellationRequested();

                    if (IsStreamClosed) break;

                    if (nextByte == -1) break;

                    if (nextByte != CarriageReturn && nextByte != LineFeed)
                        memStr.WriteByte(nextByte);

                    if (nextByte == LineFeed)
                    {
                        int byteCount = (int)memStr.Length;
                        byte[] tweetBytes = new byte[byteCount];

                        memStr.Position = 0;
                        await memStr.ReadAsync(tweetBytes, 0, byteCount, CancellationToken).ConfigureAwait(false);

                        string tweet = Encoding.UTF8.GetString(tweetBytes, 0, byteCount);
                        var strmContent = new StreamContent(this, tweet);

                        await StreamingCallbackAsync(strmContent).ConfigureAwait(false);

                        memStr.Dispose();
                        memStr = new MemoryStream();
                    }
                }
            }

            IsStreamClosed = false;

            return "{}";
        }
 
        HttpRequestMessage ConfigureRequest(Request request)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Endpoint));

            var parameters =
                string.Join("&",
                    (from parm in request.RequestParameters
                     select parm.Name + "=" + Url.PercentEncode(parm.Value))
                    .ToList());                            
            var content = new HttpStringContent(parameters,  Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/x-www-form-urlencoded");
            httpRequest.Content = content;

            var parms = request.RequestParameters
                               .ToDictionary(
                                    key => key.Name,
                                    val => val.Value);
            SetAuthorizationHeader(HttpMethod.Post, request.FullUrl, parms, httpRequest);
            httpRequest.Headers.Add("User-Agent", UserAgent);
            //httpRequest.Headers.ExpectContinue = false;

            if (Authorizer.SupportsCompression)
                httpRequest.Headers.AcceptEncoding.TryParseAdd("gzip");

            return httpRequest;
        }
 
        async Task<Stream> CreateStream(HttpResponseMessage response)
        {
            throw new NotImplementedException("CreateStream");
            //var stream = await response.Content.ReadAsInputStreamAsync(); //.ConfigureAwait(false);

            //if (Authorizer.SupportsCompression)
            //    return new GZipStream(stream, CompressionMode.Decompress);
            //else
            //    return stream;
        }

        /// <summary>
        /// Closes the stream
        /// </summary>
        public void CloseStream()
        {
            IsStreamClosed = true;

            if (StreamingClient != null) StreamingClient.Dispose();
            //    StreamingClient.CancelPendingRequests();

            
        }

        /// <summary>
        /// Performs HTTP POST media byte array upload to Twitter.
        /// </summary>
        /// <param name="url">Url to upload to.</param>
        /// <param name="postData">Request parameters.</param>
        /// <param name="data">Image to upload.</param>
        /// <param name="name">Image parameter name.</param>
        /// <param name="fileName">Image file name.</param>
        /// <param name="contentType">Type of image: must be one of jpg, gif, or png.</param>
        /// <param name="reqProc">Request processor for handling results.</param>
        /// <returns>JSON response From Twitter.</returns>
        public async Task<string> PostMediaAsync(string url, IDictionary<string, string> postData, byte[] data, string name, string fileName, string contentType, CancellationToken cancelToken)
        {
            WriteLog(url, "QueryTwitterAsync");

            var multiPartContent = new HttpMultipartFormDataContent();
            var byteArrayContent = new HttpBufferContent(data.AsBuffer());
            byteArrayContent.Headers.Add("Content-Type", contentType);
            multiPartContent.Add(byteArrayContent, name, fileName);

            var cleanPostData = new Dictionary<string, string>();

            foreach (var pair in postData)
            {
                if (pair.Value != null)
                {
                    cleanPostData.Add(pair.Key, pair.Value);
                    multiPartContent.Add(new HttpStringContent(pair.Value), pair.Key);
                }
            }

            //var handler = new PostMessageHandler(this, new Dictionary<string, string>(), url);
            var handler = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
            handler.AutomaticDecompression = true;
            using (var client = new HttpClient(handler))
            {
                InitClient(client, url, new Dictionary<string, string>(), null);
                //if (Timeout != 0)
                //    client.Timeout = new TimeSpan(0, 0, 0, Timeout);

                HttpResponseMessage msg = await client.PostAsync(new Uri(url), multiPartContent); //.ConfigureAwait(false);

                return await HandleResponseAsync(msg);
            }
        }

        protected async void InitClient(HttpClient client, string url, IDictionary<string, string> parms, HttpRequestMessage req)
        {
            if(req!=null) SetAuthorizationHeader(HttpMethod.Post, url, parms, req);
            if(client!= null) SetAuthorizationHeader(HttpMethod.Post, url, parms, client);

            var authStringParms = parms.ToDictionary(parm => parm.Key, elm => elm.Value);
            authStringParms.Add("oauth_consumer_key", Authorizer.CredentialStore.ConsumerKey);
            authStringParms.Add("oauth_token", Authorizer.CredentialStore.OAuthToken);

            

            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            //client.DefaultRequestHeaders.ExpectContinue = false;
            //client.DefaultRequestHeaders.CacheControl = { NoCache = true };

            //if (SupportsAutomaticDecompression)
            //req.Headers.comre = DecompressionMethods.GZip;
            //if (exe.Authorizer.Proxy != null && SupportsProxy)
            //    Proxy = exe.Authorizer.Proxy;

            //if (exe.ReadWriteTimeout != 0)
            //    ReadWriteTimeout = exe.ReadWriteTimeout;

            //return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// performs HTTP POST to Twitter
        /// </summary>
        /// <param name="url">URL of request</param>
        /// <param name="postData">parameters to post</param>
        /// <param name="getResult">callback for handling async Json response - null if synchronous</param>
        /// <returns>Json Response from Twitter - empty string if async</returns>
        public async Task<string> PostToTwitterAsync<T>(string url, IDictionary<string, string> postData, CancellationToken cancelToken)
        {
            WriteLog(url, "PostToTwitterAsync");

            var cleanPostData = new Dictionary<string, string>();

            var dataString = new StringBuilder();

            foreach (var pair in postData)
            {
                if (pair.Value != null)
                {
                    dataString.AppendFormat("{0}={1}&", pair.Key, Url.PercentEncode(pair.Value));
                    cleanPostData.Add(pair.Key, pair.Value);
                }
            }

            var content = new HttpStringContent(dataString.ToString().TrimEnd('&'), Windows.Storage.Streams.UnicodeEncoding.Utf8 , "application/x-www-form-urlencoded");
            //var handler = new PostMessageHandler(this, cleanPostData, url);
            var handler = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
            handler.AutomaticDecompression = true;
            using (var client = new HttpClient(handler))
            {
                //if (Timeout != 0)
                //    client.Timeout = new TimeSpan(0, 0, 0, Timeout);
                InitClient(client, url, cleanPostData, null);

                HttpResponseMessage msg = await client.PostAsync(new Uri(url), content); //.ConfigureAwait(false);

                return await HandleResponseAsync(msg);
            }
        }
  
        async Task<string> HandleResponseAsync(HttpResponseMessage msg)
        {
            LastUrl = msg.RequestMessage.RequestUri;

            ResponseHeaders =
                (from header in msg.Headers
                 select new
                 {
                     Key = header.Key,
                     Value = string.Join(", ", header.Value)
                 })
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);

            await ThrowIfErrorAsync(msg).ConfigureAwait(false);

            return await msg.Content.ReadAsStringAsync(); //.ConfigureAwait(false);
        }

        void WriteLog(string content, string currentMethod)
        {
            if (Log != null)
            {
                Log.WriteLine("--Log Starts Here--");
                Log.WriteLine("Query:" + content);
                Log.WriteLine("Method:" + currentMethod);
                Log.WriteLine("--Log Ends Here--");
                Log.Flush();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StreamingCallbackAsync = null;

                if (Log != null)
                {
                    Log.Dispose();
                }
            }
        }



        public static async Task ThrowIfErrorAsync(HttpResponseMessage msg)
        {
            const int TooManyRequests = 429;

            // TODO: research proper handling of 304

            if ((int)msg.StatusCode < 400) return;


            throw new Exception(msg.StatusCode.ToString());
        }
    }
}