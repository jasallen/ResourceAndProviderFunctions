using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Management.ResourceManager;
using fluent = Microsoft.Azure.Management.ResourceManager.Fluent;
using System.Collections.Generic;
using Microsoft.Azure.Management.ResourceManager.Models;
using System.Linq;

namespace ResourceAndProviderFunctions
{
    public static class ProvidersAndLocations
    {
        public const string RegisteredStateName = "Registered";

        private static Lazy<IResourceManagementClient> ResourceManagementClient = new Lazy<IResourceManagementClient>(InitializeSdkClient);

        [FunctionName("ProvidersAndLocations")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, ILogger log)
        {
            var providers = ListResourceProviders(null, true);

            var locationsByProvider = providers.Select(provider =>
                new {
                    provider.NamespaceProperty,
                    Locations = provider.ResourceTypes.SelectMany(resTp => resTp.Locations).Distinct(StringComparer.InvariantCultureIgnoreCase)
                }
            );
            var providersByLocation = providers.SelectMany(pvs => pvs.ResourceTypes)
                .SelectMany(resTp => resTp.Locations)
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .OrderBy(s => s)
                .Select(locS => new
                {
                    Location = locS,
                    Providers = providers
                        .Where(
                            prov => prov.ResourceTypes
                            .SelectMany(resTp => resTp.Locations)
                            .Any(l => string.Equals(l, locS, StringComparison.InvariantCultureIgnoreCase))
                        )
                        .Select(prov => prov.NamespaceProperty)
                });

            var retVal = new
            {
                locationsByProvider,
                providersByLocation
            };

            return (ActionResult)new OkObjectResult(retVal);
        }

        private static IResourceManagementClient InitializeSdkClient()
        {
            var clientId = System.Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = System.Environment.GetEnvironmentVariable("ClientSecret");
            var tenantId = System.Environment.GetEnvironmentVariable("TenantId");

            var ServicePrincipalCredentials = fluent.SdkContext.AzureCredentialsFactory
                .FromServicePrincipal(clientId,
                clientSecret,
                tenantId,
                fluent.AzureEnvironment.AzureGlobalCloud);

            var azure = Microsoft.Azure.Management.Fluent.Azure
                .Configure()
                .Authenticate(ServicePrincipalCredentials)
                .WithDefaultSubscription();

            /**********
            Alternative to authenticating below. Alt version requires passing in Subscription Id but may be
            faster and doesn't require a dependency on Fluent packages. It *does* require a dependency on 
            Microsoft.Azure.Services.AppAuthentication pkg.
            
            AzureServiceTokenProvider azureServiceTokenProvider = new AzureServiceTokenProvider();
            var serviceCreds = new TokenCredentials(await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/").ConfigureAwait(false));            
            var resourceManagementClient = new ResourceManagementClient(serviceCreds);
            **********/

            var _resourceManagementClient = new ResourceManagementClient(ServicePrincipalCredentials);
            _resourceManagementClient.SubscriptionId = azure.SubscriptionId;

            return _resourceManagementClient;
        }

        public static List<Provider> ListResourceProviders(string providerName = null, bool listAvailable = true)
        {
            if (!string.IsNullOrEmpty(providerName))
            {
                var provider = ResourceManagementClient.Value.Providers.Get(providerName);

                if (provider == null)
                {
                    throw new KeyNotFoundException(providerName + " not found");
                }

                return new List<Provider> { provider };
            }
            else
            {
                var returnList = new List<Provider>();
                var tempResult = ResourceManagementClient.Value.Providers.List(null);
                returnList.AddRange(tempResult);

                while (!string.IsNullOrWhiteSpace(tempResult.NextPageLink))
                {
                    tempResult = ResourceManagementClient.Value.Providers.ListNext(tempResult.NextPageLink);
                    returnList.AddRange(tempResult);
                }

                return listAvailable
                    ? returnList
                    : returnList.Where(IsProviderRegistered).ToList();
            }
        }

        //public List<Provider> GetRegisteredProviders(List<Provider> providers)
        //{
        //    return providers.CoalesceEnumerable().Where(this.IsProviderRegistered).ToList();
        //}

        private static bool IsProviderRegistered(Provider provider)
        {
            return string.Equals(
                RegisteredStateName,
                provider.RegistrationState,
                StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
