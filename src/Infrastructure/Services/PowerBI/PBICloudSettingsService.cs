namespace Sqlbi.Bravo.Infrastructure.Services.PowerBI
{
    using Microsoft.Win32;
    using Sqlbi.Bravo.Infrastructure;
    using Sqlbi.Bravo.Infrastructure.Configuration;
    using Sqlbi.Bravo.Infrastructure.Contracts.PBICloud;
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Helpers;
    using Sqlbi.Bravo.Infrastructure.Models.PBICloud;
    using Sqlbi.Bravo.Models;
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Mime;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IPBICloudSettingsService
    {
        IPBICloudEnvironment CloudEnvironment { get; }

        TenantCluster TenantCluster { get; }

        Task InitializeAsync(CancellationToken cancellationToken);

        Task RefreshAsync(string accessToken, CancellationToken cancellationToken);
    }

    internal class PBICloudSettingsService : IPBICloudSettingsService
    {
        private const string GlobalServiceEnvironmentsDiscoverUrl = "powerbi/globalservice/v201606/environments/discover";
        private const string GlobalServiceGetOrInsertClusterUrisByTenantlocationUrl = "spglobalservice/GetOrInsertClusterUrisByTenantlocation";

        private readonly static SemaphoreSlim _initializeSemaphore = new(1, 1);
        private readonly Lazy<Uri> _environmentDiscoverUri;
        private readonly HttpClient _httpClient;

        private IPBICloudEnvironment? _cloudEnvironment;
        private GlobalService? _globalService;
        private TenantCluster? _tenantCluster;

        public PBICloudSettingsService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _environmentDiscoverUri = new(() => GetEnvironmentDiscoverUri());
        }

        public IPBICloudEnvironment CloudEnvironment
        {
            get
            {
                BravoUnexpectedException.ThrowIfNull(_cloudEnvironment);
                return _cloudEnvironment;
            }
        }

        public TenantCluster TenantCluster
        {
            get
            {
                BravoUnexpectedException.ThrowIfNull(_tenantCluster);
                return _tenantCluster;
            }
        }

        /// <remarks>Refresh is required only if the login account has changed</remarks>
        public async Task RefreshAsync(string accessToken, CancellationToken cancellationToken)
        {
            _tenantCluster = await GetTenantClusterAsync(accessToken, cancellationToken).ConfigureAwait(false);
            //_localClientSites = Contracts.PBIDesktop.LocalClientSites.Create();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_globalService is null || _cloudEnvironment is null)
            {
                await _initializeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_globalService is null)
                        _globalService = await InitializeGlobalServiceAsync(cancellationToken).ConfigureAwait(false);

                    if (_cloudEnvironment is null)
                        _cloudEnvironment = InitializeCloudEnvironment();
                }
                finally
                {
                    _initializeSemaphore.Release();
                }
            }
        }

        private async Task<GlobalService> InitializeGlobalServiceAsync(CancellationToken cancellationToken)
        {
            var securityProtocol = ServicePointManager.SecurityProtocol;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using var request = new HttpRequestMessage(HttpMethod.Post, _environmentDiscoverUri.Value);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (AppEnvironment.IsDiagnosticLevelVerbose)
                    AppEnvironment.AddDiagnostics(DiagnosticMessageType.Json, name: $"{ nameof(PBICloudSettingsService) }.{ nameof(InitializeGlobalServiceAsync) }", content);

                var globalService = JsonSerializer.Deserialize<GlobalService>(content, AppEnvironment.DefaultJsonOptions);
                {
                    BravoUnexpectedException.ThrowIfNull(globalService);
                }
                return globalService;
            }
            finally
            {
                ServicePointManager.SecurityProtocol = securityProtocol;
            }
        }

        private IPBICloudEnvironment InitializeCloudEnvironment()
        {
            var pbicloudEnvironmentType = UserPreferences.Current.Experimental?.PBICloudEnvironment ?? PBICloudEnvironmentType.Public;
            var globalServiceCloudName = pbicloudEnvironmentType.ToGlobalServiceCloudName();
            var globalServiceEnvironment = _globalService?.Environments?.SingleOrDefault((c) => globalServiceCloudName.EqualsI(c.CloudName));

            BravoUnexpectedException.ThrowIfNull(globalServiceEnvironment);

            var pbicloudEnvironment = PBICloudEnvironment.CreateFrom(pbicloudEnvironmentType, globalServiceEnvironment);

            if (AppEnvironment.IsDiagnosticLevelVerbose)
                AppEnvironment.AddDiagnostics(DiagnosticMessageType.Json, name: $"{ nameof(PBICloudSettingsService) }.{ nameof(InitializeCloudEnvironment) }", content: JsonSerializer.Serialize(pbicloudEnvironment));

            return pbicloudEnvironment;
        }

        private async Task<TenantCluster> GetTenantClusterAsync(string accessToken, CancellationToken cancellationToken)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var request = new HttpRequestMessage(HttpMethod.Put, GlobalServiceGetOrInsertClusterUrisByTenantlocationUrl);
            request.Content = new StringContent(string.Empty, Encoding.UTF8, MediaTypeNames.Application.Json);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (AppEnvironment.IsDiagnosticLevelVerbose)
                AppEnvironment.AddDiagnostics(DiagnosticMessageType.Json, name: $"{ nameof(PBICloudSettingsService) }.{ nameof(GetTenantClusterAsync) }", content);

            var tenantCluster = JsonSerializer.Deserialize<TenantCluster>(content, AppEnvironment.DefaultJsonOptions);
            {
                BravoUnexpectedException.ThrowIfNull(tenantCluster);
            }
            return tenantCluster;
        }

        private static Uri GetEnvironmentDiscoverUri()
        {
            const string PowerBIDiscoveryUrl = "PowerBIDiscoveryUrl";

            // Values from discover v202003
            string[] TruestedDiscoverUriString = new[]
            {
                PBICloudService.PBCommercialApiUri.AbsoluteUri,                 // PBICloudEnvironmentType.Public
                "https://api.powerbi.cn",                                       // PBICloudEnvironmentType.China
                "https://api.powerbi.de",                                       // PBICloudEnvironmentType.Germany
                "https://api.powerbigov.us",                                    // PBICloudEnvironmentType.USGov
                "https://api.high.powerbigov.us",                               // PBICloudEnvironmentType.USGovHigh
                "https://api.mil.powerbigov.us",                                // PBICloudEnvironmentType.USGovMil
                //"https://biazure-int-edog-redirect.analysis-df.windows.net",  // disabled PpeCloud
                //"https://api.powerbi.eaglex.ic.gov",                          // disabled USNatCloud
                //"https://api.powerbi.microsoft.scloud",                       // disabled USSecCloud
            };

            // https://docs.microsoft.com/en-us/power-bi/enterprise/service-govus-overview#sign-in-to-power-bi-for-us-government
            // https://github.com/microsoft/Federal-Business-Applications/tree/main/whitepapers/power-bi-registry-settings

            var uriString = CommonHelper.ReadRegistryString(Registry.LocalMachine, keyName: "SOFTWARE\\Microsoft\\Microsoft Power BI", valueName: PowerBIDiscoveryUrl);

            if (uriString is null)
                uriString = CommonHelper.ReadRegistryString(Registry.LocalMachine, keyName: "SOFTWARE\\WOW6432Node\\Policies\\Microsoft\\Microsoft Power BI", valueName: PowerBIDiscoveryUrl);

            if (uriString is not null)
            {
                if (TruestedDiscoverUriString.Contains(uriString, StringComparer.OrdinalIgnoreCase) == false)
                {
                    throw new BravoUnexpectedInvalidOperationException($"Untrusted { nameof(PowerBIDiscoveryUrl) } value ({ uriString })");
                }
            }

            if (uriString is null)
                uriString = PBICloudService.PBCommercialApiUri.AbsoluteUri;

            var uriBuilder = new UriBuilder(uriString)
            {
                Path = GlobalServiceEnvironmentsDiscoverUrl,
                Query = "client=powerbi-msolap",
            };

            return uriBuilder.Uri;
        }
    }
}
