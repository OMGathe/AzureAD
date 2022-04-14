﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Instance.Discovery;
using Microsoft.Identity.Client.Internal.Broker;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.Platforms.Features.DesktopOs;
using Microsoft.Identity.Client.PlatformsCommon.Shared;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Identity.Client.Broker
{

    // TODO: x64 on WPF not working
    // TODO: need to map exceptions 
    //   - TODO: WAM's retrayble exception?
    // TODO: add logging (Blocked - a C++ API exists, no C# API yet as it's pretty complex, waiting for msalruntime to exposit it)
    // TODO: bug around double interactive auth https://identitydivision.visualstudio.com/Engineering/_workitems/edit/1858419
    // TODO: configure for MSA-PT (via extra query params - msal_request_type for the key and consumer_passthrough for the value) (Completed)

    // TODO: silent auth with default account (Completed)
    // TODO: interactive auth with default account (Completed)
    // TODO: remove account is not implemented    
    // TODO: pass in claims - try {"access_token":{"deviceid":{"essential":true}}}

    internal class RuntimeBroker : IBroker
    {
        private readonly ICoreLogger _logger;
        private readonly IntPtr _parentHandle = IntPtr.Zero;

        internal const string ErrorMessageSuffix = " For more details see https://aka.ms/msal-net-wam";
        private readonly WindowsBrokerOptions _wamOptions;

        private const string InfrastructureTenant = "f8cdef31-a31e-4b4a-93e4-5f571e91255a";
        private const string NativeInteropMsalRequestType = "msal_request_type"; 
        private const string ConsumersPassthroughRequest = "consumer_passthrough";

        /// <summary>
        /// Ctor. Only call if on Win10, otherwise a TypeLoadException occurs. See DesktopOsHelper.IsWin10
        /// </summary>
        public RuntimeBroker(
            CoreUIParent uiParent,
            ApplicationConfiguration appConfig,
            ICoreLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _parentHandle = GetParentWindow(uiParent);

            _wamOptions = appConfig.WindowsBrokerOptions ??
                WindowsBrokerOptions.CreateDefault();
        }

        /// <summary>
        /// Acquire Token Interactively 
        /// </summary>
        /// <param name="authenticationRequestParameters"></param>
        /// <param name="acquireTokenInteractiveParameters"></param>
        /// <returns></returns>
        /// <exception cref="MsalServiceException"></exception>
        public async Task<MsalTokenResponse> AcquireTokenInteractiveAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters acquireTokenInteractiveParameters)
        {
            if (_parentHandle == IntPtr.Zero)
            {
                throw new MsalClientException(
                    "window_handle_required",
                    "Public Client applications wanting to use WAM need to provide their window handle. Console applications can use GetConsoleWindow Windows API for this.");
            }

            //if OperatingSystemAccount is passed then we use the user signed-in on the machine
            if (PublicClientApplication.IsOperatingSystemAccount(authenticationRequestParameters.Account))
            {
                return await AcquireTokenInteractiveDefaultUserAsync(authenticationRequestParameters, acquireTokenInteractiveParameters).ConfigureAwait(false);
            }

            var cancellationToken = authenticationRequestParameters.RequestContext.UserCancellationToken;

            _logger.Verbose("[WamBroker] Using Windows account picker.");

            bool isMsaPassthrough = _wamOptions.MsaPassthrough;

            var authority = isMsaPassthrough ?
                authenticationRequestParameters.Authority.GetTenantedAuthority(InfrastructureTenant, true) :
                authenticationRequestParameters.Authority.AuthorityInfo.CanonicalAuthority;

            MsalTokenResponse msalTokenResponse = null;

            using (var core = new NativeInterop.Core())
            using (var authParams = new NativeInterop.AuthParameters(authenticationRequestParameters.AppConfig.ClientId, authority))
            {
                authParams.RequestedScopes = string.Join(" ", authenticationRequestParameters.Scope);
                authParams.RedirectUri = authenticationRequestParameters.RedirectUri.ToString();

                if(isMsaPassthrough)
                    authParams.Properties[NativeInteropMsalRequestType] = ConsumersPassthroughRequest;

                string loginHint = authenticationRequestParameters.LoginHint ?? authenticationRequestParameters?.Account?.Username;

                if (!string.IsNullOrEmpty(loginHint))
                {
                    _logger.Verbose("[WamBroker] AcquireTokenInteractive - account information provided. Trying to find a Windows account that matches.");
                }
                else
                {
                    _logger.Verbose("[WamBroker] Account information was not provided. Using an account picker.");
                }

                using (var result = await core.SignInInteractivelyAsync(
                    _parentHandle,
                    authParams,
                    authenticationRequestParameters.CorrelationId.ToString("D"),
                    loginHint,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (result.IsSuccess)
                    {
                        msalTokenResponse = ParseRuntimeResponse(result, authenticationRequestParameters);
                        _logger.Verbose("[WamBroker] Successfully retrieved token.");

                    }
                    else
                    {
                        _logger.Error($"[WamBroker] Could not login interactively. {result.Error}");
                        throw new MsalServiceException("wam_interactive_failed", $"Could not get the account provider - account picker. {result.Error}");
                    }
                }
            }

            return msalTokenResponse;
        }

        /// <summary>
        /// AcquireToken Interactively for the default user using WAM
        /// </summary>
        /// <param name="authenticationRequestParameters"></param>
        /// <param name="acquireTokenInteractiveParameters"></param>
        /// <returns></returns>
        /// <exception cref="MsalServiceException"></exception>
        public async Task<MsalTokenResponse> AcquireTokenInteractiveDefaultUserAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenInteractiveParameters acquireTokenInteractiveParameters)
        {
            if (_parentHandle == IntPtr.Zero)
            {
                throw new MsalClientException(
                    "window_handle_required",
                    "Public Client applications wanting to use WAM need to provide their window handle. Console applications can use GetConsoleWindow Windows API for this.");
            }

            var cancellationToken = authenticationRequestParameters.RequestContext.UserCancellationToken;

            _logger.Verbose("[WamBroker] Signing in with the default user account.");

            bool isMsaPassthrough = _wamOptions.MsaPassthrough;

            var authority = isMsaPassthrough ?
                authenticationRequestParameters.Authority.GetTenantedAuthority(InfrastructureTenant, true) :
                authenticationRequestParameters.Authority.AuthorityInfo.CanonicalAuthority;

            MsalTokenResponse msalTokenResponse = null;

            using (var core = new NativeInterop.Core())
            using (var authParams = new NativeInterop.AuthParameters(authenticationRequestParameters.AppConfig.ClientId, authority))
            {
                authParams.RequestedScopes = string.Join(" ", authenticationRequestParameters.Scope);
                authParams.RedirectUri = authenticationRequestParameters.RedirectUri.ToString();

                if (isMsaPassthrough)
                    authParams.Properties[NativeInteropMsalRequestType] = ConsumersPassthroughRequest;

                using (NativeInterop.AuthResult result = await core.SignInAsync(
                        _parentHandle,
                        authParams,
                        authenticationRequestParameters.CorrelationId.ToString("D"),
                        cancellationToken).ConfigureAwait(false))
                {
                    if (result.IsSuccess)
                    {
                        msalTokenResponse = ParseRuntimeResponse(result, authenticationRequestParameters);
                    }
                    else
                    {
                        _logger.Error($"[WamBroker] Could not login interactively with the Default OS Account. {result.Error}");
                        throw new MsalServiceException("wam_interactive_failed", $"Could not get the account provider for the default OS Account. {result.Error}");
                    }
                }
            }

            return msalTokenResponse;
        }

        /// <summary>
        /// Parse Native Interop AuthResult Response to MSAL Token Response
        /// </summary>
        /// <param name="authResult"></param>
        /// <param name="authenticationRequestParameters"></param>
        /// <returns></returns>
        /// <exception cref="MsalServiceException"></exception>
        private MsalTokenResponse ParseRuntimeResponse(
                NativeInterop.AuthResult authResult, AuthenticationRequestParameters authenticationRequestParameters)
        {
            try
            {
                string expiresOn = authResult.ExpiresOn.ToString();
                string correlationId = authenticationRequestParameters.CorrelationId.ToString("D");

                if (string.IsNullOrWhiteSpace(correlationId))
                {
                    _logger.Warning("No correlation ID in response");
                    correlationId = null;
                }

                MsalTokenResponse msalTokenResponse = new MsalTokenResponse()
                {
                    AccessToken = authResult.AccessToken,
                    IdToken = authResult.IdToken,
                    CorrelationId = correlationId,
                    Scope = authResult.GrantedScopes,
                    ExpiresIn = DateTimeHelpers.GetDurationFromWindowsTimestamp(expiresOn, _logger),
                    ClientInfo = authResult.Account.ClientInfo.ToString(),
                    TokenType = "Bearer",
                    WamAccountId = authResult.Account.Id,
                    TokenSource = TokenSource.Broker
                };

                _logger.Info("WAM response status success");

                return msalTokenResponse;
            }
            catch (NativeInterop.MsalRuntimeException ex)
            {
                throw new MsalServiceException("wam_failed", $"Could not acquire token using WAM. {ex.Message}");
            }

        }

        /// <summary>
        /// Gets the window handle
        /// </summary>
        /// <param name="uiParent"></param>
        /// <returns></returns>
        private IntPtr GetParentWindow(CoreUIParent uiParent)
        {

            if (uiParent?.OwnerWindow is IntPtr ptr)
            {
                return ptr;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Auth Broker Installation URL
        /// </summary>
        /// <param name="appLink"></param>
        /// <exception cref="NotImplementedException"></exception>
        public void HandleInstallUrl(string appLink)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Check to see if broker is installed and invokable
        /// </summary>
        /// <param name="authorityType"></param>
        /// <returns></returns>
        public bool IsBrokerInstalledAndInvokable(AuthorityType authorityType)
        {
            if (!DesktopOsHelper.IsWin10OrServerEquivalent())
            {
                _logger.Warning("[WAM Broker] Not a supported operating system. WAM broker is not available. ");
                return false;
            }

            // WAM does not work on pure ADFS environments
            if (authorityType == AuthorityType.Adfs)
            {
                _logger.Warning("[WAM Broker] WAM does not work in pure ADFS environments. Falling back to browser for an ADFS authority. ");
                return false;
            }

            _logger.Verbose("[WAM Broker] IsBrokerInstalledAndInvokable true");
            return true;
        }

        /// <summary>
        /// AcquireTokenSilentAsync
        /// </summary>
        /// <param name="authenticationRequestParameters"></param>
        /// <param name="acquireTokenSilentParameters"></param>
        /// <returns></returns>
        /// <exception cref="MsalUiRequiredException"></exception>
        public async Task<MsalTokenResponse> AcquireTokenSilentAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            var cancellationToken = authenticationRequestParameters.RequestContext.UserCancellationToken;

            _logger.Verbose("[WamBroker] Acquiring token silently.");

            bool isMsaPassthrough = _wamOptions.MsaPassthrough;

            var authority = isMsaPassthrough ?
                authenticationRequestParameters.Authority.GetTenantedAuthority(InfrastructureTenant, true) :
                authenticationRequestParameters.Authority.AuthorityInfo.CanonicalAuthority;

            MsalTokenResponse msalTokenResponse = null;

            using (var core = new NativeInterop.Core())
            using (var authParams = new NativeInterop.AuthParameters(authenticationRequestParameters.AppConfig.ClientId, authority))
            {
                authParams.RequestedScopes = string.Join(" ", authenticationRequestParameters.Scope);
                authParams.RedirectUri = authenticationRequestParameters.RedirectUri.ToString();

                if (isMsaPassthrough)
                    authParams.Properties[NativeInteropMsalRequestType] = ConsumersPassthroughRequest;

                using (var account = await core.ReadAccountByIdAsync(
                    acquireTokenSilentParameters.Account.HomeAccountId.ObjectId,
                    authenticationRequestParameters.CorrelationId.ToString("D"),
                    cancellationToken).ConfigureAwait(false))
                {
                    if (account == null)
                    {
                        _logger.WarningPii(
                            $"Could not find a WAM account for the selected user {acquireTokenSilentParameters.Account.Username}",
                            "Could not find a WAM account for the selected user");

                        throw new MsalUiRequiredException(
                            "wam_no_account_for_id",
                            $"Could not find a WAM account for the selected user {acquireTokenSilentParameters.Account.Username}");
                    }

                    using (NativeInterop.AuthResult result = await core.AcquireTokenSilentlyAsync(
                        authParams,
                        authenticationRequestParameters.CorrelationId.ToString("D"),
                        account,
                        cancellationToken).ConfigureAwait(false))
                    {
                        if (result.IsSuccess)
                        {
                            msalTokenResponse = ParseRuntimeResponse(result, authenticationRequestParameters);
                        }
                        else
                        {
                            throw new MsalUiRequiredException(MsalError.FailedToAcquireTokenSilentlyFromBroker, $"Failed to acquire token silently. {result.Error}");
                        }
                    }
                }
            }

            return msalTokenResponse;
        }

        /// <summary>
        /// Acquire Token Silent with Default User
        /// </summary>ter
        /// <param name="authenticationRequestParameters"></param>
        /// <param name="acquireTokenSilentParameters"></param>
        /// <returns></returns>
        /// <exception cref="MsalUiRequiredException"></exception>
        public async Task<MsalTokenResponse> AcquireTokenSilentDefaultUserAsync(
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenSilentParameters acquireTokenSilentParameters)
        {
            var cancellationToken = authenticationRequestParameters.RequestContext.UserCancellationToken;

            _logger.Verbose("[WamBroker] Acquiring token silently for default account.");

            bool isMsaPassthrough = _wamOptions.MsaPassthrough;

            var authority = isMsaPassthrough ?
                authenticationRequestParameters.Authority.GetTenantedAuthority(InfrastructureTenant, true) :
                authenticationRequestParameters.Authority.AuthorityInfo.CanonicalAuthority;

            MsalTokenResponse msalTokenResponse = null;

            using (var core = new NativeInterop.Core())
            using (var authParams = new NativeInterop.AuthParameters(authenticationRequestParameters.AppConfig.ClientId, authority))
            {
                authParams.RequestedScopes = string.Join(" ", authenticationRequestParameters.Scope);
                authParams.RedirectUri = authenticationRequestParameters.RedirectUri.ToString();

                if (isMsaPassthrough)
                    authParams.Properties[NativeInteropMsalRequestType] = ConsumersPassthroughRequest;

                using (NativeInterop.AuthResult result = await core.SignInSilentlyAsync(
                        authParams,
                        authenticationRequestParameters.CorrelationId.ToString("D"),
                        cancellationToken).ConfigureAwait(false))
                {
                    if (result.IsSuccess)
                    {
                        msalTokenResponse = ParseRuntimeResponse(result, authenticationRequestParameters);
                    }
                    else
                    {
                        throw new MsalUiRequiredException(MsalError.FailedToAcquireTokenSilentlyFromBroker, $"Failed to acquire token silently. {result.Error}");
                    }
                }
            }

            return msalTokenResponse;
        }

        /// <summary>
        /// GetAccountsAsync
        /// </summary>
        /// <param name="clientID"></param>
        /// <param name="redirectUri"></param>
        /// <param name="authorityInfo"></param>
        /// <param name="cacheSessionManager"></param>
        /// <param name="instanceDiscoveryManager"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<IReadOnlyList<IAccount>> GetAccountsAsync(
            string clientID,
            string redirectUri,
            AuthorityInfo authorityInfo,
            ICacheSessionManager cacheSessionManager,
            IInstanceDiscoveryManager instanceDiscoveryManager)
        {
            // runtime does not yet support account discovery

            return Task.FromResult<IReadOnlyList<IAccount>>(Array.Empty<IAccount>());
        }

        /// <summary>
        /// RemoveAccountAsync
        /// </summary>
        /// <param name="appConfig"></param>
        /// <param name="account"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task RemoveAccountAsync(ApplicationConfiguration appConfig, IAccount account)
        {
            throw new NotImplementedException();
        }
    }
}