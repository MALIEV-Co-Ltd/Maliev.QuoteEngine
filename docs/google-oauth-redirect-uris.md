# Google OAuth Redirect URIs

QuoteEngine uses two Google OAuth web clients. Keep the redirect URIs in Google
Cloud Console aligned with the callback path used by each flow.

## MALIEV Sign-In - Shared

Use this client for customer sign-in through Web, QuoteEngine, and Intranet.
QuoteEngine normal sign-in uses ASP.NET Google authentication with callback path
`/auth/google/signin`.

Required QuoteEngine redirect URIs:

```text
https://localhost:7297/auth/google/signin
http://localhost:5012/auth/google/signin
https://make.maliev.com/auth/google/signin
```

Do not use the Google Drive callback URI for customer sign-in. Google requires
the `redirect_uri` in the authorization request to exactly match a redirect URI
registered on this same sign-in client.

## MALIEV QuoteEngine - Google Drive Connector

Use this dedicated client for the QuoteEngine Google Drive connector only. It
requests Drive-scoped user authorization and completes on the Drive callback
path.

Required QuoteEngine redirect URIs:

```text
https://localhost:7297/auth/google/drive/callback
http://localhost:5012/auth/google/drive/callback
https://make.maliev.com/auth/google/drive/callback
```

This client must not be used for customer sign-in.
