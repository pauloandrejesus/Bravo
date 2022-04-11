namespace Sqlbi.Bravo.Services
{
    using Sqlbi.Bravo.Infrastructure;
    using Sqlbi.Bravo.Infrastructure.Models;
    using Sqlbi.Bravo.Infrastructure.Services.PowerBI;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IAuthenticationService
    {
        Uri PBICloudTenantCluster { get; }

        IAuthenticationResult PBICloudAuthentication { get; }

        Task<bool> IsPBICloudSignInRequiredAsync(CancellationToken cancellationToken);

        Task PBICloudSignInAsync(string userPrincipalName, CancellationToken cancellationToken);

        Task PBICloudSignOutAsync(CancellationToken cancellationToken);
    }

    internal class AuthenticationService : IAuthenticationService
    {
        private readonly IPBICloudAuthenticationService _pbicloudAuthenticationService;

        public AuthenticationService(IPBICloudAuthenticationService pbicloudAuthenticationService)
        {
            _pbicloudAuthenticationService = pbicloudAuthenticationService;
        }

        public Uri PBICloudTenantCluster => _pbicloudAuthenticationService.TenantCluster;

        public IAuthenticationResult PBICloudAuthentication
        {
            get
            {
                BravoUnexpectedException.ThrowIfNull(_pbicloudAuthenticationService.AuthenticationResult);
                return _pbicloudAuthenticationService.AuthenticationResult;
            }
        }

        public async Task<bool> IsPBICloudSignInRequiredAsync(CancellationToken cancellationToken)
        {
            if (_pbicloudAuthenticationService.AuthenticationResult is null)
            {
                // SignIn required - an interaction is required with the end user of the application, for instance:
                // - no refresh token was in the cache
                // - the user needs to consent or re-sign-in (for instance if the password expired)
                // - the user needs to perform two factor auth
                return true;
            }

            if (_pbicloudAuthenticationService.AuthenticationResult.IsExpired)
            {
                await PBICloudSignInAsync(_pbicloudAuthenticationService.AuthenticationResult.Account.UserPrincipalName, cancellationToken).ConfigureAwait(false);
            }

            return false;  // No SignIn required - cached token is valid
        }

        public async Task PBICloudSignInAsync(string userPrincipalName, CancellationToken cancellationToken)
        {
            try
            {
                await _pbicloudAuthenticationService.SignInAsync(userPrincipalName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BravoException(BravoProblem.SignInMsalTimeoutExpired);
            }
        }

        public async Task PBICloudSignOutAsync(CancellationToken cancellationToken)
        {
            await _pbicloudAuthenticationService.SignOutAsync(cancellationToken).ConfigureAwait(false);
            BravoUnexpectedException.Assert(_pbicloudAuthenticationService.AuthenticationResult is null);
        }
    }
}