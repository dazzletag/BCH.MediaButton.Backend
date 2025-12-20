import { PublicClientApplication, type Configuration } from "@azure/msal-browser";
import { appConfig } from "./config";

const authority = appConfig.auth.tenantId
  ? `https://login.microsoftonline.com/${appConfig.auth.tenantId}`
  : "https://login.microsoftonline.com/common";

const msalConfig: Configuration = {
  auth: {
    clientId: appConfig.auth.clientId,
    authority,
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: "localStorage",
    storeAuthStateInCookie: true,
  },
};

export const loginRequest = {
  scopes: [appConfig.auth.scope].filter(Boolean),
};

export const msalInstance = new PublicClientApplication(msalConfig);
