using System;
using Stylelabs.M.Sdk.WebClient;

namespace LinkedInConnector.Utils
{
    public static class MConnector
    {
        private static Lazy<IWebMClient> _client { get; set; }

        public static IWebMClient Client
        {
            get
            {
                if (_client != null) return _client.Value;
                var auth = new Stylelabs.M.Sdk.WebClient.Authentication.OAuthPasswordGrant()
                {
                    ClientId = AppSettings.ClientId,
                    ClientSecret = AppSettings.ClientSecret,
                    UserName = AppSettings.Username,
                    Password = AppSettings.Password
                };

                _client = new Lazy<IWebMClient>(() => MClientFactory.CreateMClient(AppSettings.Host, auth));

                return _client.Value;
            }
        }
    }
}
