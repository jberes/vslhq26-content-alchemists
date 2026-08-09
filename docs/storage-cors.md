# Storage uploads — why they no longer depend on CORS

> **Resolved.** Kit uploads now go **through the API**, not from the browser to blob storage,
> so the storage account's CORS rules no longer gate them. This document is kept because the
> diagnosis is worth not repeating, and because the SAS endpoints still exist for large media.

## What was happening

Uploading a brand asset failed in the app while the API was perfectly healthy.

Uploads did not go through the API. The API minted a short-lived SAS URL and the **browser**
`PUT` the bytes straight to Azure Blob Storage — which makes every upload a **cross-origin
request to `*.blob.core.windows.net`**, rejected unless the account carries a matching CORS
rule.

Proven by sending the browser's own preflight:

```
$ curl -i -X OPTIONS "<sas-url>" \
    -H "Origin: http://localhost:5084" \
    -H "Access-Control-Request-Method: PUT" \
    -H "Access-Control-Request-Headers: x-ms-blob-type,content-type"

HTTP/1.1 403 CORS not enabled or no matching rule found for this request.
<Error><Code>CorsPreflightFailure</Code>…</Error>
```

The same SAS `PUT` from a server returned **201**. Account, credentials, container and SAS were
all fine — only the browser path was blocked, which is why `/health` and a green API told us
nothing.

## Why a CORS rule was not the real fix

Adding the rule fixed the web shell (preflight `403 → 200`, upload `201`). It did **not** fix
every shell: origins are matched exactly, and the MAUI WebView does not share the web client's
origin. That makes uploads depend on a per-shell setting living outside this repo — a standing
source of "works for me".

## The fix

`POST /api/v1/blob/assets/{assetId}/content` streams the body into the asset's private blob
server-side. It is the **same origin every other API call already uses**, so if the app can
reach the API, it can upload — from the web client, the desktop shell, or a test.

Verified by **clearing every CORS rule from the account** and uploading two files from a real
browser: `via API 204 POST` twice, both assets landed. The only direct-to-storage traffic left
is `<img>` thumbnail loads, which are not subject to CORS.

Kit images are small and infrequent, so holding them in a request costs nothing. The SAS
endpoints remain for large media, where it would not.

## The CORS rule, if you still want it

Currently applied to `castmill` (belt-and-braces; nothing requires it today):

```bash
az storage cors add --services b \
  --methods GET PUT OPTIONS HEAD \
  --origins "http://localhost:5084" "https://localhost:7124" \
  --allowed-headers "x-ms-blob-type" "x-ms-blob-content-type" "content-type" \
  --exposed-headers "*" --max-age 3600 --account-name castmill
```

If anything is ever moved back onto the direct SAS path, note that `x-ms-blob-type` and
`OPTIONS` are each individually mandatory, and origins match on scheme, host **and** port.
