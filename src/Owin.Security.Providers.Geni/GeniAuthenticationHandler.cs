﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler.Encoder;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Owin.Security.Providers.Geni.Provider;

namespace Owin.Security.Providers.Geni
{
    public class GeniAuthenticationHandler : AuthenticationHandler<GeniAuthenticationOptions>
    {
        private const string XmlSchemaString = "http://www.w3.org/2001/XMLSchema#string";

        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        public GeniAuthenticationHandler(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            AuthenticationProperties properties = null;

            try
            {
                string code = null;
                string state = null;

                var query = Request.Query;
                var values = query.GetValues("code");
                if (values != null && values.Count == 1)
                {
                    code = values[0];
                }
                values = query.GetValues("state");
                if (values != null && values.Count == 1)
                {
                    state = values[0];
                }

                properties = Options.StateDataFormat.Unprotect(state);
                if (properties == null)
                {
                    return null;
                }

                // OAuth2 10.12 CSRF
                if (!ValidateCorrelationId(properties, _logger))
                {
                    return new AuthenticationTicket(null, properties);
                }

                var requestPrefix = Request.Scheme + "://" + Request.Host;
                var redirectUri = requestPrefix + Request.PathBase + Options.CallbackPath;

                // Build up the body for the token request
                var body = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("client_id", Options.AppKey),
                    new KeyValuePair<string, string>("redirect_uri", redirectUri)
                };

                // Request the token
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, Options.Endpoints.TokenEndpoint);
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", new Base64TextEncoder().Encode(Encoding.ASCII.GetBytes(Options.AppKey + ":" + Options.AppSecret)));
                requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                requestMessage.Content = new FormUrlEncodedContent(body);
                var tokenResponse = await _httpClient.SendAsync(requestMessage);
                tokenResponse.EnsureSuccessStatusCode();
                var text = await tokenResponse.Content.ReadAsStringAsync();

                // Deserializes the token response
                dynamic response = JsonConvert.DeserializeObject<dynamic>(text);
                var accessToken = (string)response.access_token;
                var refreshToken = (string) response.refresh_token;

                // Get the user info
                var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, Options.Endpoints.UserEndpoint);
                userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                userInfoRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var userInfoResponse = await _httpClient.SendAsync(userInfoRequest);
                userInfoResponse.EnsureSuccessStatusCode();
                text = await userInfoResponse.Content.ReadAsStringAsync();
                var user = JObject.Parse(text);

                var context = new GeniAuthenticatedContext(Context, user, accessToken, refreshToken)
                {
                    Identity = new ClaimsIdentity(
                        Options.AuthenticationType,
                        ClaimsIdentity.DefaultNameClaimType,
                        ClaimsIdentity.DefaultRoleClaimType)
                };
                if (!string.IsNullOrEmpty(context.Id))
                {
                    context.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, context.Id, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(context.Name))
                {
                    context.Identity.AddClaim(new Claim(ClaimsIdentity.DefaultNameClaimType, context.Name, XmlSchemaString, Options.AuthenticationType));
                }
                context.Properties = properties;

                await Options.Provider.Authenticated(context);

                return new AuthenticationTicket(context.Identity, context.Properties);
            }
            catch (Exception ex)
            {
                _logger.WriteError(ex.Message);
            }
            return new AuthenticationTicket(null, properties);
        }

        protected override Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode != 401)
            {
                return Task.FromResult<object>(null);
            }

            var challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

            if (challenge == null) return Task.FromResult<object>(null);
            var baseUri =
                Request.Scheme +
                Uri.SchemeDelimiter +
                Request.Host +
                Request.PathBase;

            var currentUri =
                baseUri +
                Request.Path +
                Request.QueryString;

            var redirectUri =
                baseUri +
                Options.CallbackPath;

            var properties = challenge.Properties;
            if (string.IsNullOrEmpty(properties.RedirectUri))
            {
                properties.RedirectUri = currentUri;
            }

            // OAuth2 10.12 CSRF
            GenerateCorrelationId(properties);

            var state = Options.StateDataFormat.Protect(properties);

            var authorizationEndpoint =
                Options.Endpoints.AuthorizationEndpoint +
                "?client_id=" + Uri.EscapeDataString(Options.AppKey) +
                "&response_type=" + Uri.EscapeDataString("code") +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&state=" + Uri.EscapeDataString(state);

            Response.Redirect(authorizationEndpoint);

            return Task.FromResult<object>(null);
        }

        public override async Task<bool> InvokeAsync()
        {
            return await InvokeReplyPathAsync();
        }

        private async Task<bool> InvokeReplyPathAsync()
        {
            if (!Options.CallbackPath.HasValue || Options.CallbackPath != Request.Path) return false;
            // TODO: error responses

            var ticket = await AuthenticateAsync();
            if (ticket == null)
            {
                _logger.WriteWarning("Invalid return state, unable to redirect.");
                Response.StatusCode = 500;
                return true;
            }

            var context = new GeniReturnEndpointContext(Context, ticket)
            {
                SignInAsAuthenticationType = Options.SignInAsAuthenticationType,
                RedirectUri = ticket.Properties.RedirectUri
            };

            await Options.Provider.ReturnEndpoint(context);

            if (context.SignInAsAuthenticationType != null &&
                context.Identity != null)
            {
                var grantIdentity = context.Identity;
                if (!string.Equals(grantIdentity.AuthenticationType, context.SignInAsAuthenticationType, StringComparison.Ordinal))
                {
                    grantIdentity = new ClaimsIdentity(grantIdentity.Claims, context.SignInAsAuthenticationType, grantIdentity.NameClaimType, grantIdentity.RoleClaimType);
                }
                Context.Authentication.SignIn(context.Properties, grantIdentity);
            }

            if (context.IsRequestCompleted || context.RedirectUri == null) return context.IsRequestCompleted;
            var redirectUri = context.RedirectUri;
            if (context.Identity == null)
            {
                // add a redirect hint that sign-in failed in some way
                redirectUri = WebUtilities.AddQueryString(redirectUri, "error", "access_denied");
            }
            Response.Redirect(redirectUri);
            context.RequestCompleted();

            return context.IsRequestCompleted;
        }
    }
}