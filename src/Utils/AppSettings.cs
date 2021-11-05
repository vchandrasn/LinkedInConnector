using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace LinkedInConnector.Utils
{
    public static class AppSettings
    {
        private static IConfiguration _config;

        private static IConfiguration Configuration
        {
            get
            {
                if (_config == null)
                {
                    var builder = new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("AppSettings.json");

                    _config = builder.Build();
                }

                return _config;
            }
        }

        public static Uri Host => new Uri($"{Configuration["Values:MHost"]}");
        public static string ClientId => $"{Configuration["Values:MClientId"]}";
        public static string ClientSecret => $"{Configuration["Values:MClientSecret"]}";
        public static string Username => $"{Configuration["Values:MUsername"]}";
        public static string Password => $"{Configuration["Values:MPassword"]}";
        public static string LinkedInPersonId => $"{Configuration["Values:LinkedInPersonId"]}";
        public static string LinkedInOAuthToken => $"{Configuration["Values:LinkedInOAuthToken"]}";
    }
}
