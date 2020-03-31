using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Configuration;
using System.Web.Mvc;
using Umbraco.Web.Mvc;
using Umbraco.Web.WebApi;

namespace FastlyUmbraco.v7
{
	[PluginController("FastlyUmbraco")]
	public class FastlyAPIController : UmbracoAuthorizedApiController
	{
		[System.Web.Http.AcceptVerbs("POST")]
		[HttpPost]
		public async Task<HttpResponseMessage> PurgeURLByIDAsync()
		{
			string contentId = await Request.Content.ReadAsStringAsync();

			HttpResponseMessage response = null;
			if (string.IsNullOrWhiteSpace(contentId) == false)
			{
				string domain = WebConfigurationManager.AppSettings["Fastly:DomainName"];

				if (string.IsNullOrWhiteSpace(domain))
				{
					response = this.Request.CreateResponse(HttpStatusCode.InternalServerError);
				} else
				{
					string url = domain + Umbraco.Content(contentId).Url();
					response = await FastlyUmbraco.SendAsync(new Uri(url));
				}				
			}
			else
			{
				response = this.Request.CreateResponse(HttpStatusCode.BadRequest);
			}

			return response;
		}
	}
}