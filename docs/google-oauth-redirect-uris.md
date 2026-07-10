# Google OAuth Redirect URIs

QuoteEngine uses Google Identity Services (GIS) for customer sign-in and a
separate OAuth web client for Google Drive. Keep the two contracts separate in
Google Cloud Console.

## MALIEV Sign-In - Google Identity Services

Use the shared MALIEV sign-in client ID with the official GIS-rendered button.
GIS posts a signed ID credential to the app BFF; normal sign-in has no OAuth
redirect callback and does not require a client secret in the browser or BFF.

Authorized JavaScript origins for QuoteEngine:

```text
https://localhost:7297
http://localhost:5012
https://make.maliev.com
```

Do not add `/auth/google/signin` redirect URIs for GIS. The BFF obtains a
single-use nonce from AuthService and sends the credential, nonce, and exact
`quote-engine` application binding to AuthService for verification.

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
