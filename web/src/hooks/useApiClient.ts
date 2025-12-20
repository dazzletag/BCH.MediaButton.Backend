import axios, { type AxiosRequestConfig } from "axios";
import { useCallback } from "react";
import { useMsal } from "@azure/msal-react";
import { appConfig } from "../config";
import { loginRequest } from "../msalConfig";

export function useApiClient() {
  const { instance, accounts } = useMsal();

  const call = useCallback(
    async <T>(config: AxiosRequestConfig) => {
      const account = accounts[0];
      if (!account) {
        throw new Error("User is not signed in.");
      }

      const tokenResponse = await instance.acquireTokenSilent({
        ...loginRequest,
        account,
      });

      const api = axios.create({
        baseURL: appConfig.apiBaseUrl,
        headers: {
          Authorization: `Bearer ${tokenResponse.accessToken}`,
        },
      });

      const response = await api.request<T>(config);
      return response.data;
    },
    [accounts, instance]
  );

  return { call };
}
