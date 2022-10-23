﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

namespace Alastack.Authentication.Hawk.AspNetCore
{
    /// <summary>
    /// Authentication handler for Hawk authentication.
    /// </summary>
    public class HawkHandler : AuthenticationHandler<HawkOptions>
    {
        /// <summary>
        /// The handler calls methods on the events which give the application control at certain points where processing is occurring. 
        /// If it is not provided a default instance is supplied which does nothing when the methods are called.
        /// </summary>
        protected new HawkEvents Events
        {
            get { return (HawkEvents)base.Events!; }
            set { base.Events = value; }
        }

        /// <summary>
        /// Initializes a new instance of <see cref="HawkHandler"/>.
        /// </summary>
        /// <inheritdoc />
        public HawkHandler(IOptionsMonitor<HawkOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock) : base(options, logger, encoder, clock)
        {
        }

        /// <inheritdoc />
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization))
            {
                return HandleFailureAuthenticateResult("Missing Authorization header.");
            }
            var authorHeaderValue = authorization.First();
            if (!authorHeaderValue.StartsWith(HawkDefaults.AuthenticationScheme))
            {
                return HandleFailureAuthenticateResult("Error authorization scheme.");
            }

            HawkParameters authParams;
            try
            {
                var rawParams = Options.AuthorizationParameterExtractor.Extract(authorHeaderValue);
                authParams = HawkParameters.Parse(rawParams);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Invalid authorization parameters.");
                return HandleFailureAuthenticateResult("Invalid authorization parameters.");
            }
            if (Options.MaxReplayRequestAge > 0 && Options.ReplayRequestValidator != null)
            {
                var result = await Options.ReplayRequestValidator.ValidateAsync(authParams.Id, authParams.Nonce, authParams.Ts, Options.MaxReplayRequestAge);
                if (result)
                {
                    return HandleFailureAuthenticateResult("Replay request.");
                }
            }

            var credential = await Options.CredentialProvider.GetCredentialAsync(authParams.Id);
            if (credential == null)
            {
                return HandleFailureAuthenticateResult("Invalid credential.");
            }

            var crypto = Options.CryptoFactory.Create(credential.HmacAlgorithm, credential.HashAlgorithm, credential.AuthKey);

            if (Options.RequireTimeSkewValidation)
            {
                var serverTimestamp = Clock.UtcNow.ToUnixTimeMilliseconds() + Options.TimeOffset;
                if (Math.Abs(serverTimestamp - authParams.Ts * 1000) > Options.TimeSkew * 1000)
                {
                    var ts = serverTimestamp / 1000;
                    var tsm = crypto.CalculateTsMac(ts);
                    return HandleFailureAuthenticateResult("Stale timestamp.", ts, tsm);
                }
            }

            var host = Options.HostResolver.Resolve(Request, Options.ForwardIndex);
            var hawkRawData = new HawkRawData
            {
                Timestamp = authParams.Ts,
                Nonce = authParams.Nonce,
                Method = Request.Method,
                Resource = $"{Request.PathBase.ToUriComponent()}{Request.Path.ToUriComponent()}{Request.QueryString.ToUriComponent()}",
                Host = host.Host,
                Port = host.Port!.Value,
                Hash = authParams.Hash,
                Ext = authParams.Ext,
                App = authParams.App,
                Dlg = authParams.Dlg
            };

            var mac = crypto.CalculateRequestMac(hawkRawData);
            if (!authParams.Mac.Equals(mac, StringComparison.Ordinal))
            {
                return HandleFailureAuthenticateResult("Bad mac.");
            }

            if (authParams.Hash != null)
            {
                Context.Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body, encoding: Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var payload = await reader.ReadToEndAsync();
                try
                {
                    var contentType = Request.ContentType?.Split(';')[0];
                    var payloadHash = crypto.CalculatePayloadHash(payload, contentType);
                    if (!authParams.Hash.Equals(payloadHash, StringComparison.Ordinal))
                    {
                        return HandleFailureAuthenticateResult("Bad payload hash.");
                    }
                }
                finally
                {
                    Request.Body.Position = 0;
                }
            }

            var identity = new ClaimsIdentity(ClaimsIssuer);
            //var identity = new ClaimsIdentity(ClaimsIssuer, ClaimTypes.Name, ClaimTypes.Role);
            var ticket = await CreateTicketAsync(identity, new AuthenticationProperties(), credential);

            if (Options.EnableServerAuthorization && credential.EnableServerAuthorization)
            {
                Context.Items["hawk:credential"] = credential;
                Context.Items["hawk:hawkRawData"] = hawkRawData;
            }

            return AuthenticateResult.Success(ticket);
            //return AuthenticateResult.Fail("Authentication failed");
        }

        /// <inheritdoc />
        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (!Options.DisableChallenge && Context.Items.TryGetValue("hawk:www-authenticate", out var wwwAuthenticate))
            {
                Response.Headers["WWW-Authenticate"] = (string)wwwAuthenticate!;
            }
            await base.HandleChallengeAsync(properties);
        }

        /// <summary>
        /// Creates an <see cref="AuthenticationTicket"/> from the specified <paramref name="credential"/>.
        /// </summary>
        /// <param name="identity">The <see cref="ClaimsIdentity"/>.</param>
        /// <param name="properties">The <see cref="AuthenticationProperties"/>.</param>
        /// <param name="credential">The <see cref="HawkCredential"/>.</param>
        /// <returns>The <see cref="AuthenticationTicket"/>.</returns>
        protected virtual async Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, HawkCredential credential)
        {
            var claims = new[] { new Claim(ClaimTypes.Name, credential.User ?? credential.AuthId), new Claim(ClaimTypes.NameIdentifier, credential.AuthId) };
            identity.AddClaims(claims);
            var context = new HawkCreatingTicketContext(new ClaimsPrincipal(identity), properties, Context, Scheme, Options);
            await Events.CreatingTicket(context);
            return new AuthenticationTicket(context.Principal!, context.Properties, Scheme.Name);
        }

        protected virtual AuthenticateResult HandleFailureAuthenticateResult(string failureMessage, long? ts = null, string? tsm = null)
        {
            if (!Options.DisableChallenge)
            {
                var t = ts == null ? String.Empty : $" ts=\"{ts}\", tsm=\"{tsm}\",";
                var wwwAuthenticate = $"{HawkDefaults.AuthenticationScheme}{t} error=\"{failureMessage}\"";
                Context.Items["hawk:www-authenticate"] = wwwAuthenticate;
            }
            return AuthenticateResult.Fail(failureMessage);
        }
    }
}