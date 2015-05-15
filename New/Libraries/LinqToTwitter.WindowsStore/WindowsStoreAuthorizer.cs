using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Security.Authentication.Web;
using Windows.Web.Http;

namespace LinqToTwitter.WindowsStore
{
    public class WindowsStoreAuthorizer : AuthorizerBase, IAuthorizer
    {
        public async Task AuthorizeAsync()
        {
            if (CredentialStore == null)
                throw new NullReferenceException(
                    "The authorization process requires a minimum of ConsumerKey and ConsumerSecret tokens. " +
                    "You must assign the CredentialStore property (with tokens) before calling AuthorizeAsync().");

            if (CredentialStore.HasAllCredentials()) return;

            if (string.IsNullOrWhiteSpace(CredentialStore.ConsumerKey) || string.IsNullOrWhiteSpace(CredentialStore.ConsumerSecret))
                throw new ArgumentException("You must populate CredentialStore with ConsumerKey and ConsumerSecret tokens before calling AuthorizeAsync.", "CredentialStore");

            await GetRequestTokenAsync(Callback);

            string authUrl = PrepareAuthorizeUrl(ForceLogin);

            WebAuthenticationResult webAuthenticationResult =
                await WebAuthenticationBroker.AuthenticateAsync(
                    WebAuthenticationOptions.None,
                    new Uri(authUrl),
                    new Uri(Callback));

            if (webAuthenticationResult.ResponseStatus == WebAuthenticationStatus.Success)
            {
                string verifier = ParseVerifierFromResponseUrl(webAuthenticationResult.ResponseData);
                var accessTokenParams = new Dictionary<string, string>();
                accessTokenParams.Add("oauth_verifier", verifier);

                await GetAccessTokenAsync(accessTokenParams);
            }
        }

        public async override Task<string> HttpGetAsync(string oauthUrl, IDictionary<string, string> parameters)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(oauthUrl));
            req.Headers.Add("Authorization", GetAuthorizationString(System.Net.Http.HttpMethod.Get, oauthUrl, parameters));
            req.Headers.Add("User-Agent", UserAgent);
            //req.Headers.ExpectContinue = false;

            var handler = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
            handler.AutomaticDecompression = true; ;
            //if (Proxy != null && handler.SupportsProxy)
            //    handler.Proxy = Proxy;

            var msg = await new HttpClient(handler).SendRequestAsync(req);//.ConfigureAwait(false);

            await ThrowIfErrorAsync(msg).ConfigureAwait(false);

            return await msg.Content.ReadAsStringAsync(); //.ConfigureAwait(false);
        }

        public async override Task<string> HttpPostAsync(string oauthUrl, IDictionary<string, string> parameters)
        {
            var postData =
                (from keyValPair in parameters
                 where !keyValPair.Key.StartsWith("oauth")
                 select keyValPair)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(oauthUrl));
            req.Headers.Add("Authorization", GetAuthorizationString(System.Net.Http.HttpMethod.Post, oauthUrl, parameters));
            req.Headers.Add("User-Agent", UserAgent);
            //req.Headers.ExpectContinue = false;

            var paramString =
                string.Join("&",
                    (from parm in postData
                     select parm.Key + "=" + Net.Url.PercentEncode(parm.Value))
                    .ToList());
            var content = new HttpStringContent(paramString, Windows.Storage.Streams.UnicodeEncoding.Utf8 , "application/x-www-form-urlencoded");
            req.Content = content;

            var handler = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
            handler.AutomaticDecompression = true;
            //if (Proxy != null && handler.SupportsProxy)
            //    handler.Proxy = Proxy;

            var msg = await new HttpClient(handler).SendRequestAsync(req); //.ConfigureAwait(false);

            await ThrowIfErrorAsync(msg).ConfigureAwait(false);

            return await msg.Content.ReadAsStringAsync(); //.ConfigureAwait(false);
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
