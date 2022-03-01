namespace Sqlbi.Bravo.Infrastructure.Middleware
{
    using Microsoft.AspNetCore.Http;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Yarp.ReverseProxy.Forwarder;

    internal class ReverseProxyTelemetryTransformer : HttpTransformer
    {
        public const string PathSegments = "/proxy/telemetry/";
        public const string ForwarderDestinationPrefix = "https://dc.services.visualstudio.com/";

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix);

            var path = httpContext.Request.Path;
            var query = httpContext.Request.QueryString;

            if (httpContext.Request.Path.StartsWithSegments(PathSegments, out var remainingPath))
                path = remainingPath;

            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, path, query);
            proxyRequest.Headers.Host = null;
        }
    } 
}
