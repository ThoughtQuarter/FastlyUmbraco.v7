using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web;
using System.Web.Configuration;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Publishing;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.Routing;

namespace FastlyUmbraco.v7
{
    public class FastlyUmbraco : ApplicationEventHandler
    {
        private static readonly HttpClient Client;
        private static readonly int MaxAge;

        private const string DoNotCacheControlPropertyName = "fastlyDoNotCache";
        private const string CacheForPropertyName = "fastlyCacheFor";

        private const string FastlyApplicationIdKey = "Fastly:ApplicationId";
        private const string FastlyMaxAgeKey = "Fastly:MaxAge";
        private const string FastlyApiKey = "Fastly:ApiKey";
        private const string FastlyStaleWhileInvalidateKey = "Fastly:StaleWhileInvalidate";
        private const string FastlyStaleIfError = "Fastly:StaleIfError";
        private const string FastlyPurgeAllOnPublishKey = "Fastly:PurgeAllOnPublish";
        private const string FastlyDisableAzureARRAffinityKey = "Fastly:DisableAzureARRAffinity";
        private const string FastlyDomainKey = "Fastly:DomainName";
        private const string FastlyApiDelayTime = "Fastly:ApiDelayTime";

        private List<Uri> urlsToPurge = new List<Uri>();

        static FastlyUmbraco()
        {
            //Client = new HttpClient { BaseAddress = new Uri("https://api.fastly.com/") };
            Client = new HttpClient();

            Client.DefaultRequestHeaders.Accept.Clear();
            Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Client.DefaultRequestHeaders.Add("Fastly-Key", WebConfigurationManager.AppSettings[FastlyApiKey]);

            if (WebConfigurationManager.AppSettings.AllKeys.Contains(FastlyMaxAgeKey))
            {
                int.TryParse(WebConfigurationManager.AppSettings[FastlyMaxAgeKey], out MaxAge);
            }
        }

        //Inital setup and event subscription
        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            CreateWebConfigSettings();

            PublishedContentRequest.Prepared += ConfigurePublishedContentRequestCaching;

            bool purgeOnPublish;
            bool.TryParse(WebConfigurationManager.AppSettings[FastlyPurgeAllOnPublishKey], out purgeOnPublish);
            if (purgeOnPublish)
            {
                ContentService.Publishing += SetUrlsToPurge;
                ContentService.Published += PurgeUrl;
                ContentService.UnPublishing += SetUrlsToPurge;
                ContentService.UnPublished += PurgeUrl;
            }
        }        

        public static void CreateWebConfigSettings()
        {
            var settings = new Dictionary<string, object>();

            settings.Add(FastlyDomainKey, GetAppSetting(FastlyDomainKey, "www.example.com"));
            settings.Add(FastlyApplicationIdKey, GetAppSetting(FastlyApplicationIdKey, " "));
            settings.Add(FastlyApiKey, GetAppSetting(FastlyApiKey, " "));
            settings.Add(FastlyMaxAgeKey, GetAppSetting(FastlyMaxAgeKey, "3600"));
            settings.Add(FastlyPurgeAllOnPublishKey, GetAppSetting(FastlyPurgeAllOnPublishKey, "true"));
            settings.Add(FastlyStaleWhileInvalidateKey, GetAppSetting(FastlyStaleWhileInvalidateKey, "30"));
            settings.Add(FastlyStaleIfError, GetAppSetting(FastlyStaleIfError, "86400"));
            settings.Add(FastlyDisableAzureARRAffinityKey, GetAppSetting(FastlyDisableAzureARRAffinityKey, "true"));
            settings.Add(FastlyApiDelayTime, GetAppSetting(FastlyApiDelayTime, "10000"));

            var config = WebConfigurationManager.OpenWebConfiguration("/");

            bool shouldSave = false;
			foreach (var setting in settings)
			{
				if (!config.AppSettings.Settings.AllKeys.Contains(setting.Key))
				{
					config.AppSettings.Settings.Add(setting.Key, setting.Value.ToString());
                    shouldSave = true;
                }
			}

            if ( shouldSave)
            {
                config.Save(ConfigurationSaveMode.Minimal);
            }
        }

        public static void RemoveWebConfigSettings()
        {
            var config = WebConfigurationManager.OpenWebConfiguration("/");

            string[] settings = new string[] {
                FastlyDomainKey,
                FastlyApplicationIdKey,
                FastlyApiKey,
                FastlyMaxAgeKey,
                FastlyPurgeAllOnPublishKey,
                FastlyStaleWhileInvalidateKey,
                FastlyStaleIfError,
                FastlyDisableAzureARRAffinityKey,
                FastlyApiDelayTime
            };

            bool shouldSave = false;
            foreach (var setting in settings)
			{
				if (config.AppSettings.Settings.AllKeys.Contains(setting))
				{
					config.AppSettings.Settings.Remove(setting);
                    shouldSave = true;
                }
			}

            if (shouldSave)
            {
                config.Save(ConfigurationSaveMode.Minimal);
            }
        }

        private static string GetAppSetting(string key, string defaultValue)
        {
            var appSettings = WebConfigurationManager.AppSettings;
            return appSettings.AllKeys.Contains(key) ? appSettings[key] : defaultValue;
        }

        //Prepublishing / preunpublishing
        private void SetUrlsToPurge(IPublishingStrategy sender, PublishEventArgs<IContent> e)
        {
            var helper = new UmbracoHelper(UmbracoContext.Current);
            string domain = WebConfigurationManager.AppSettings[FastlyDomainKey];

            //Loop through content to publish and store the URLs for after publish finishes
            foreach (IContent content in e.PublishedEntities)
            {
                if (content.Status == ContentStatus.Published)
                {
                    IPublishedContent publishedContent = (IPublishedContent) helper.Content(content.Id);
                    urlsToPurge.Add(new Uri(domain + publishedContent.Url));
                }
            }
        }

        //After publish / unpublish
        private async void PurgeUrl(IPublishingStrategy sender, PublishEventArgs<IContent> e)
        {
            foreach (Uri url in urlsToPurge)
            {
                LogHelper.Info(this.GetType(), "Fastly - Purge URL Called - " + url);
                await SendAsync(url);
            }
            urlsToPurge.Clear();
        }

        public static async Task<HttpResponseMessage> SendAsync(Uri requestUri)
        {
            HttpResponseMessage response = null;

            int apiDelayTimeValue = 5000;
            if (int.TryParse(WebConfigurationManager.AppSettings[FastlyApiDelayTime], out int apiDelayTimeOut) && apiDelayTimeOut >= 0)
            {
                apiDelayTimeValue = apiDelayTimeOut;
            }

            Type classType = typeof(FastlyUmbraco);

            await System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var request = new HttpRequestMessage(new HttpMethod("PURGE"), requestUri);
                    LogHelper.Info(classType, "Fastly - Purge URL Request - " + request.ToString().Replace("{", "(").Replace("}", ")"));

                    await System.Threading.Tasks.Task.Delay(apiDelayTimeValue);
                    LogHelper.Info(classType, "Fastly - Purge URL Delay");

                    response = await Client.SendAsync(request);
                    LogHelper.Info(classType, "Fastly - Purge URL Response - " + response.ToString().Replace("{", "(").Replace("}", ")"));
                }
                catch (Exception e)
                {
                    LogHelper.Error(classType, "Fastly - Exception - ", e);
                }
            });

            return response;
        }

        private void PurgeAll(IPublishingStrategy strategy, PublishEventArgs<IContent> e)
        {
            var appId = WebConfigurationManager.AppSettings[FastlyApplicationIdKey];
            using (var task = Client.PostAsync($"service/{appId}/purge_all", new StringContent("")))
                task.Wait();
        }

        //Catch request to published content to edit cache control headers
        private void ConfigurePublishedContentRequestCaching(object sender, EventArgs eventArgs)
        {
            var req = sender as PublishedContentRequest;
            var res = HttpContext.Current.Response;

            if (req == null || req.HasPublishedContent == false) return;
            if (HttpContext.Current == null) return;

            if (req.RoutingContext.UmbracoContext.InPreviewMode == false)
            {
                var content = req.PublishedContent;
                var maxAge = MaxAge;

                if (content.HasProperty(DoNotCacheControlPropertyName) && content.HasValue(DoNotCacheControlPropertyName))
                {
                    if (content.GetPropertyValue<bool>(DoNotCacheControlPropertyName) == false)
                    {
                        //Set do Cache control headers here
                        if (content.HasProperty(CacheForPropertyName) && content.HasValue(CacheForPropertyName))
                        {
                            maxAge = content.GetPropertyValue<int>(CacheForPropertyName);
                        }

                        if (maxAge <= 0) return;

                        res.AppendHeader("x-Backend-Name", "todo");
                        res.AppendHeader("Surrogate-Control", "max-age=" + maxAge);
                        res.Cache.SetCacheability(HttpCacheability.NoCache);

                        // stale while invalidate - https://docs.fastly.com/guides/performance-tuning/serving-stale-content
                        int staleWhileInvalidate;
                        if (int.TryParse(WebConfigurationManager.AppSettings[FastlyStaleWhileInvalidateKey], out staleWhileInvalidate) && staleWhileInvalidate > 0)
                        {
                            res.Cache.AppendCacheExtension($"stale-while-revalidate={staleWhileInvalidate}");
                        }

                        // stale if error - https://docs.fastly.com/en/guides/serving-stale-content#manually-enabling-serve-stale
                        int staleIfError;
                        if (int.TryParse(WebConfigurationManager.AppSettings[FastlyStaleIfError], out staleIfError) && staleIfError > 0)
                        {
                            res.Cache.AppendCacheExtension($"stale-if-error={staleIfError}");
                        }

                        // disable ARRAffinity Set-Cookie, which results in a cache miss - UNDERSTAND THE IMPLICATIONS OF THIS ON AZURE
                        bool disableARRAffinity;
                        if (bool.TryParse(WebConfigurationManager.AppSettings[FastlyDisableAzureARRAffinityKey], out disableARRAffinity) && disableARRAffinity)
                        {
                            //res.Headers.Add("Arr-Disable-Session-Affinity", "True");
                        }
                    }
                    else
                    {
                        //Set do no cache control headers here
                        res.Cache.SetCacheability(HttpCacheability.Private);
                        res.Cache.SetNoStore();
                    }
                } else
                {
                    //Set do no cache control headers here
                    res.Cache.SetCacheability(HttpCacheability.Private);
                    res.Cache.SetNoStore();
                }
            }
        }
    }
}