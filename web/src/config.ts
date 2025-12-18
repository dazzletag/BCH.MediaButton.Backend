export type AppConfig = {
  apiBaseUrl: string;
  auth: {
    clientId: string;
    tenantId: string;
    scope: string;
  };
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
const clientId = import.meta.env.VITE_AUTH_CLIENT_ID as string | undefined;
const tenantId = import.meta.env.VITE_AUTH_TENANT_ID as string | undefined;
const scopeEnv = import.meta.env.VITE_AUTH_SCOPE as string | undefined;

if (!apiBaseUrl || !clientId || !tenantId) {
  console.warn(
    "Missing one or more required environment variables: VITE_API_BASE_URL, VITE_AUTH_CLIENT_ID, VITE_AUTH_TENANT_ID."
  );
}

export const appConfig: AppConfig = {
  apiBaseUrl: apiBaseUrl ?? "",
  auth: {
    clientId: clientId ?? "",
    tenantId: tenantId ?? "",
    scope: scopeEnv ?? (clientId ? `api://${clientId}/access_as_user` : ""),
  },
};
