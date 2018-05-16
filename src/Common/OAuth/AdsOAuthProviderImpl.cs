﻿// Copyright 2018 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Ads.Common.Lib;
using Google.Api.Ads.Common.Logging;
using Google.Api.Ads.Common.Util;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Util;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Api.Ads.Common.OAuth {
  /// <summary>
  /// Default implementation of OAuth2 provider.
  /// </summary>
  public class AdsOAuthProviderImpl : AdsOAuthProviderForServiceAccounts,
      AdsOAuthProviderForApplications {

    #region properties

    /// <summary>
    /// The feature ID for this class.
    /// </summary>
    private const AdsFeatureUsageRegistry.Features FEATURE_ID =
        AdsFeatureUsageRegistry.Features.OAuthServiceAccountFlow;

    /// <summary>
    /// The registry for saving feature usage information..
    /// </summary>
    protected readonly AdsFeatureUsageRegistry featureUsageRegistry =
        AdsFeatureUsageRegistry.Instance;

    private readonly TimeSpan OAUTH2_EXPIRATION_CUTOFF = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The HttpClientFactory used for requesting access tokens.
    /// </summary>
    public IHttpClientFactory HttpClientFactory { get; set; }

    /// <summary>
    /// The clock used for requesting access tokens.
    /// </summary>
    public IClock Clock { get; set; } = SystemClock.Default;

    /// <summary>
    /// Gets the application configuration class for this object.
    /// </summary>
    public AppConfig Config { get; }

    /// <summary>
    /// Gets or sets the client that is making the request. This value is obtained from the
    /// <a href="http://console.developers.google.com">Google Cloud Console</a> during
    /// application registration.
    /// </summary>
    public string ClientId {
      get {
        return Config.OAuth2ClientId;
      } set {
        Config.OAuth2ClientId = value;
      }
    }

    /// <summary>
    /// Gets or sets the client secret obtained from the <a href="http://console.developers.google.com">
    /// Google Cloud Console</a> during application registration.
    /// </summary>
    public string ClientSecret {
      get {
        return Config.OAuth2ClientSecret;
      }
      set {
        Config.OAuth2ClientSecret = value;
      }
    }

    /// <summary>
    /// Gets or sets the API access your application is requesting. This is space delimited.
    /// </summary>
    public string Scope {
      get {
        return Config.OAuth2Scope;
      }
      set {
        Config.OAuth2Scope = value;
      }
    }

    /// <summary>
    /// Gets or sets a parameter that your application can use for keeping state. The OAuth
    /// authorization server roundtrips this parameter.
    /// </summary>
    public string State { get; set; }

    /// <summary>
    /// Gets the type of token returned by the server. This field will always have the value
    /// Bearer for now.
    /// </summary>
    public string TokenType { get; }

    /// <summary>
    /// Gets or sets the token that can be sent to a Google API for authentication.
    /// </summary>
    public string AccessToken {
      get {
        return Config.OAuth2AccessToken;
      } set {
        Config.OAuth2AccessToken = value;
      }
    }

    /// <summary>
    /// Gets or sets the time at which access token was retrieved.
    /// </summary>
    public DateTime UpdatedOn { get; set; }

    /// <summary>
    /// Gets the remaining lifetime on the access token.
    /// </summary>
    public int ExpiresIn {
      get {
        return (int) ExpiresInDuration.TotalSeconds;
      }
      set {
        ExpiresInDuration = TimeSpan.FromSeconds(value);
      }
    }

    /// <summary>
    /// Gets the remaining lifetime on the access token, as a timespan.
    /// </summary>
    private TimeSpan ExpiresInDuration {
      // TODO (Anash): Rename this to ExpiresIn in a future breaking change.
      get; set;
    }

    /// <summary>
    /// Callback triggered when this provider obtains a new access token or refresh token from
    /// the OAuth server.
    /// </summary>
    public OAuthTokensObtainedCallback OnOAuthTokensObtained { get; set; }

    /// <summary>
    /// Indicates if your application needs to access APIs when the user is not present at the
    /// browser. This is defaulted to true.
    /// </summary>
    public bool IsOffline { get; set; } = true;

    /// <summary>
    /// Gets or sets where the url where the response is sent. This should be a registered
    /// redirect uri on the <a href="http://console.developers.google.com">Google Cloud
    /// Console</a>.
    /// </summary>
    public string RedirectUri {
      get {
        return Config.OAuth2RedirectUri;
      } set {
        Config.OAuth2RedirectUri = value;
      }
    }

    /// <summary>
    /// Gets or sets a token that may be used to obtain a new access token. Refresh tokens are
    /// valid until the user revokes access.
    /// </summary>
    public string RefreshToken {
      get {
        return Config.OAuth2RefreshToken;
      }
      set {
        Config.OAuth2RefreshToken = value;
      }
    }

    /// <summary>
    /// Gets the service account email for which access token should be retrieved.
    /// </summary>
    public string ServiceAccountEmail {
      get {
        return Config.OAuth2ServiceAccountEmail;
      }
    }

    /// <summary>
    /// Gets or sets the email of the account for which the call is being made.
    /// </summary>
    public string PrnEmail {
      get {
        return Config.OAuth2PrnEmail;
      }
      set {
        Config.OAuth2PrnEmail = value;
      }
    }

    /// <summary>
    /// Gets or sets the JWT private key.
    /// </summary>
    public string JwtPrivateKey {
      get {
        return Config.OAuth2PrivateKey;
      }
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsOAuthProvider"/> class.
    /// </summary>
    /// <param name="config">The configuration.</param>
    public AdsOAuthProviderImpl(AppConfig config) {
      this.Config = config;
      this.HttpClientFactory = new AdsHttpClientFactory(config);
    }

    #region interface methods.

    /// <summary>
    /// Gets the authorization URL.
    /// </summary>
    public string GetAuthorizationUrl() {
      // Mark the usage.
      featureUsageRegistry.MarkUsage(FEATURE_ID);
      ValidateOAuth2Parameter("Scope", Scope);
      ValidateOAuth2Parameter("RedirectUri", RedirectUri);
      return CreateAuthorizationUrl(RedirectUri);
    }

    /// <summary>
    /// Fetches the access and optionally the refresh token if applicable.
    /// </summary>
    /// <param name="authorizationCode">The authorization code returned by OAuth server.</param>
    /// <returns>True if the tokens were fetched successfully, false otherwise.</returns>
    public bool FetchAccessAndRefreshTokens(string authorizationCode) {
      featureUsageRegistry.MarkUsage(FEATURE_ID);
      ValidateOAuth2Parameter("AuthorizationCode", authorizationCode);

      try {
        ExtractTokensFromTaskResult(ExchangeCodeForToken(authorizationCode), true);
        return true;
      } catch (AggregateException e) {
        throw CreateOAuthException(e, "Failed to exchange authorization code for access token.");
      }
    }

    /// <summary>
    /// Refreshes the access token if offline mode is used.
    /// </summary>
    public void RefreshAccessTokenInOfflineMode() {
      // Mark the usage.
      featureUsageRegistry.MarkUsage(FEATURE_ID);
      ValidateOAuth2Parameter("RefreshToken", RefreshToken);

      try {
        ExtractTokensFromTaskResult(RefreshAccessToken(RefreshToken), false);
      } catch (AggregateException e) {
        throw CreateOAuthException(e, "Failed to refresh access token.");
      }
    }

    /// <summary>
    /// Revokes the refresh token if offline mode is used.
    /// </summary>
    public void RevokeRefreshToken() {
      // Mark the usage.
      featureUsageRegistry.MarkUsage(FEATURE_ID);
      ValidateOAuth2Parameter("RefreshToken", RefreshToken);

      try {
        RevokeRefreshToken(RefreshToken);
      } catch (AggregateException e) {
        throw CreateOAuthException(e, "Failed to revoke refresh token.");
      }
    }

    /// <summary>
    /// Generates the access token for service account.
    /// </summary>
    public void GenerateAccessTokenForServiceAccount() {
      featureUsageRegistry.MarkUsage(FEATURE_ID);

      ValidateOAuth2Parameter("ServiceAccountEmail", ServiceAccountEmail);
      ValidateOAuth2Parameter("JwtPrivateKey", JwtPrivateKey);
      ValidateOAuth2Parameter("Scope", Scope);

      try {
        ExtractTokensFromTaskResult(GetAccessTokenForServiceAccount(), false);
      } catch (AggregateException e) {
        throw CreateOAuthException(e, "Failed to generate access token in service account flow.");
      }
    }

    /// <summary>
    /// Refreshes the access token if expiring.
    /// </summary>
    public void RefreshAccessToken() {
      // Mark the usage.
      featureUsageRegistry.MarkUsage(FEATURE_ID);

      if (Config.OAuth2Mode == OAuth2Flow.APPLICATION) {
        ValidateOAuth2Parameter("RefreshToken", RefreshToken);
        if (!IsOffline) {
          throw new ArgumentException(CommonErrorMessages.OAuth2IsNotInOfflineMode);
        }
        RefreshAccessTokenInOfflineMode();
      } else {
        GenerateAccessTokenForServiceAccount();
      }
    }

    /// <summary>
    /// Refreshes the access token if expiring.
    /// </summary>
    public void RefreshAccessTokenIfExpiring() {
      // TODO (Anash): Mark as deprecated in a followup CL.
      if (IsAccessTokenExpiring()) {
        RefreshAccessToken();
      }
    }

    /// <summary>
    /// Gets the OAuth authorization header to be used with HTTP requests.
    /// </summary>
    /// <returns>The authorization header.</returns>
    public virtual string GetAuthHeader() {
      RefreshAccessTokenIfExpiring();
      return $"Bearer {AccessToken}";
    }

    #endregion

    #region private helper methods.

    /// <summary>
    /// Extracts the tokens from task result.
    /// </summary>
    /// <param name="response">The token response.</param>
    /// <param name="extractRefreshToken">True, if refresh token should be extracted from the token
    /// response, False otherwise.</param>
    private void ExtractTokensFromTaskResult(TokenResponse response, bool extractRefreshToken) {
      this.AccessToken = response.AccessToken;
      this.ExpiresInDuration = TimeSpan.FromSeconds(response.ExpiresInSeconds.GetValueOrDefault());

      if (extractRefreshToken) {
        this.RefreshToken = response.RefreshToken;
      }
      UpdatedOn = response.IssuedUtc;
      OnOAuthTokensObtained?.Invoke(this);
    }

    /// <summary>
    /// Determines whether the access token is expiring.
    /// </summary>
    /// <returns>True if the access token is expiring, false otherwise.</returns>
    private bool IsAccessTokenExpiring() {
      if (this.UpdatedOn == DateTime.MinValue) {
        return true;
      }
      return (this.UpdatedOn + this.ExpiresInDuration - OAUTH2_EXPIRATION_CUTOFF)
          < DateTime.UtcNow;
    }

    /// <summary>
    /// Validates if a required OAuth2 parameter is null or empty.
    /// </summary>
    /// <param name="propertyName">Name of the property.</param>
    /// <param name="propertyValue">The property value.</param>
    private static void ValidateOAuth2Parameter(string propertyName, string propertyValue) {
      if (string.IsNullOrEmpty(propertyValue)) {
        throw new ArgumentNullException(
            string.Format(CommonErrorMessages.OAuth2ParameterErrorMessage, propertyName));
      }
    }

    /// <summary>
    /// Creates an authentication exception from an <see cref="AggregateException"/>.
    /// </summary>
    /// <param name="e">The aggregate exception from the server.</param>
    /// <param name="message">The error message.</param>
    /// <returns>The Ads OAuth exception for rethrowing.</returns>
    private AdsOAuthException CreateOAuthException(AggregateException e, string message) {
      TokenResponseException innerException = e.InnerExceptions.FirstOrDefault()
          as TokenResponseException;
      if (innerException != null) {
        return new AdsOAuthException(message, e, innerException.Error, innerException.StatusCode);
      } else {
        return new AdsOAuthException($"{message} See inner exception for details.", e);
      }
    }

    #endregion

    #region Google.Auth interfacing code.

    /// <summary>
    /// Gets the authorization code flow.
    /// </summary>
    private GoogleAuthorizationCodeFlow AuthorizationCodeFlow {
      get {
        ValidateOAuth2Parameter("ClientId", ClientId);
        ValidateOAuth2Parameter("ClientSecret", ClientSecret);

        return new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer {
              ClientSecrets = new ClientSecrets() {
                ClientId = ClientId,
                ClientSecret = ClientSecret,
              },
              Clock = this.Clock,
              Scopes = Scope.Split(' '),
              HttpClientFactory = HttpClientFactory,
              // Set the state parameter so we can distinguish between a normal
              // page load and a callback.
              UserDefinedQueryParams = new KeyValuePair<string, string>[] {
                new KeyValuePair<string, string>("state", State)
              }
            });
      }
    }

    /// <summary>
    /// Gets the service account flow.
    /// </summary>
    private ServiceAccountCredential ServiceAccountFlow {
      get {
        ValidateOAuth2Parameter("ServiceAccountEmail", ServiceAccountEmail);
        ValidateOAuth2Parameter("JwtPrivateKey", JwtPrivateKey);

        return new ServiceAccountCredential(
            new ServiceAccountCredential.Initializer(ServiceAccountEmail) {
              Scopes = Config.OAuth2Scope.Split(' '),
              HttpClientFactory = this.HttpClientFactory,
              Clock = this.Clock,
              User = string.IsNullOrEmpty(PrnEmail) ? null : PrnEmail,
            }.FromPrivateKey(JwtPrivateKey)
        );
      }
    }

    /// <summary>
    /// Creates the authorization URL.
    /// </summary>
    /// <param name="redirectUri">The redirect URI.</param>
    /// <returns>The authorization URL.</returns>
    protected virtual string CreateAuthorizationUrl(string redirectUri) {
      Uri requestUrl = this.AuthorizationCodeFlow.CreateAuthorizationCodeRequest(
                RedirectUri).Build();

      if (IsOffline) {
        requestUrl = new GoogleAuthorizationCodeRequestUrl(requestUrl).Build();
      }

      return requestUrl.AbsoluteUri;
    }

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <returns>The token response.</returns>
    protected virtual TokenResponse ExchangeCodeForToken(string code) {
      Task<TokenResponse> task = this.AuthorizationCodeFlow.ExchangeCodeForTokenAsync(
          String.Empty, code, this.RedirectUri, CancellationToken.None);
      task.Wait();
      return task.Result;
    }

    /// <summary>
    /// Refreshes the access token.
    /// </summary>
    /// <param name="refreshToken">The refresh token.</param>
    /// <returns>The token response.</returns>
    protected virtual TokenResponse RefreshAccessToken(string refreshToken) {
      Task<TokenResponse> task = this.AuthorizationCodeFlow.RefreshTokenAsync(
          String.Empty, refreshToken, CancellationToken.None);
      task.Wait();
      return task.Result;
    }

    /// <summary>
    /// Gets the access token in service account flow.
    /// </summary>
    /// <returns>The token response.</returns>
    protected virtual TokenResponse GetAccessTokenForServiceAccount() {
      ServiceAccountCredential credential = this.ServiceAccountFlow;
      Task<string> task = credential.GetAccessTokenForRequestAsync();
      task.Wait();
      return credential.Token;
    }

    /// <summary>
    /// Revokes the refresh token asynchronously.
    /// </summary>
    /// <param name="refreshToken">The refresh token.</param>
    protected virtual void RevokeRefreshToken(string refreshToken) {
      Task task = this.AuthorizationCodeFlow.RevokeTokenAsync(String.Empty, refreshToken,
          CancellationToken.None);
      task.Wait();
    }

    #endregion
  }
}
