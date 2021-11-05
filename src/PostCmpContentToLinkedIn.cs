using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using LinkedInConnector.Utils;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Stylelabs.M.Framework.Essentials.LoadConfigurations;
using Stylelabs.M.Framework.Essentials.LoadOptions;
using Stylelabs.M.Sdk.Contracts.Base;
using Stylelabs.M.Sdk.WebClient.Http;

namespace LinkedInConnector
{
    public static class PostCmpContentToLinkedIn
    {
        private static string LinkedInAuthToken = AppSettings.LinkedInOAuthToken;

        [FunctionName("PostCmpContentToLinkedIn")]
        public static async Task Run([ServiceBusTrigger("linkedIn", "sch", Connection = "ServiceBusConnection")]string mySbMsg, ILogger log)
        {
            try
            {
                // Connectivity check
                var message = JToken.Parse(mySbMsg)["saveEntityMessage"];

                // Extract target id from request header
                var targetId = message["TargetId"].Value<long>();
                log.LogInformation($"Target Id: {targetId}");

                var entityLoadConfiguration = new EntityLoadConfiguration
                {
                    PropertyLoadOption = new PropertyLoadOption(LoadOption.All),
                    RelationLoadOption = new RelationLoadOption(new string[]
                    {
                        Constants.Content.Relations.CmpContentToLinkedAsset,
                        Constants.Content.Relations.CmpContentToMasterLinkedAsset
                    })
                };

                //Get the entity information from the TargetId
                var entity = await MConnector.Client.Entities.GetAsync(targetId, entityLoadConfiguration).ConfigureAwait(false);

                if (entity?.Id == null) return;

                //Retrieve the Name property from the entity
                var title = entity.GetPropertyValue<string>("Content.Name");

                //Get the assets associated with the CMP entity
                var contentToLinkedAssetRelation = entity.GetRelation(Constants.Content.Relations.CmpContentToLinkedAsset);

                //Log a message if there are no assets selected for the CMP entity, create a linkedIn post without image
                if (contentToLinkedAssetRelation == null || contentToLinkedAssetRelation.GetIds().Count == 0)
                {
                    log.LogInformation($"No assets selected with CMP content with ID: {entity.Id}.");

                    //Create LinkedIn post without image
                    await PostContent(title).ConfigureAwait(false);
                }
                else
                {
                    //Get the master asset for the CMP entity
                    var contentToMasterLinkedAssetRelation = entity.GetRelation(Constants.Content.Relations.CmpContentToMasterLinkedAsset);

                    IRendition rendition;

                    //Log a message if there is no master asset selected for the CMP content
                    if (contentToMasterLinkedAssetRelation == null || contentToMasterLinkedAssetRelation.GetIds().Count == 0)
                    {
                        log.LogInformation($"No master asset selected with CMP content with ID: {entity.Id}.");

                        //Pick the first asset
                        var assetEntity = await MConnector.Client.Entities
                            .GetAsync(contentToLinkedAssetRelation.GetIds().FirstOrDefault()).ConfigureAwait(false);

                        //Get the rendition associated with the asset
                        rendition = assetEntity.GetRendition(Constants.RenditionPreview) ??
                                    assetEntity.GetRendition(Constants.RenditionOriginal);
                    }
                    else
                    {
                        //Get master asset entity
                        var masterAssetEntity = await MConnector.Client.Entities
                            .GetAsync(contentToMasterLinkedAssetRelation.GetIds().FirstOrDefault()).ConfigureAwait(false);

                        //Get the rendition associated with the master asset
                        rendition = masterAssetEntity.GetRendition(Constants.RenditionPreview) ??
                                    masterAssetEntity.GetRendition(Constants.RenditionOriginal);
                    }

                    //Create LinkedIn post with image
                    await PostContentWithImage(rendition, title, log).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, ex.Message);
                log.LogError(ex, ex.ToString());
                throw;
            }
        }

        private static async Task PostContentWithImage(IRendition rendition, string title, ILogger log)
        {
            string uploadUrl, asset;

            //Register the image upload
            using (var uploadClient = new HttpClient())
            {
                uploadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LinkedInAuthToken);
                uploadClient.BaseAddress = new Uri(Constants.BaseLinkedInUrl);

                var registerUploadJsonString = $@"{{
                                                   ""registerUploadRequest"":{{
                                                      ""owner"":""urn:li:person:{AppSettings.LinkedInPersonId}"",
                                                      ""recipes"":[
                                                         ""urn:li:digitalmediaRecipe:feedshare-image""
                                                      ],
                                                      ""serviceRelationships"":[
                                                         {{
                                                            ""identifier"":""urn:li:userGeneratedContent"",
                                                            ""relationshipType"":""OWNER""
                                                         }}
                                                      ],
                                                      ""supportedUploadMechanism"":[
                                                         ""SYNCHRONOUS_UPLOAD""
                                                      ]
                                                   }}
                                                }}";

                var content = new StringContent(registerUploadJsonString, Encoding.UTF32, "application/json");
                var response = await uploadClient.PostAsync("/v2/assets?action=registerUpload", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsJsonAsync().ConfigureAwait(false);
                
                //Get the upload URL from the response
                uploadUrl = responseJson["value"]["uploadMechanism"]["com.linkedin.digitalmedia.uploading.MediaUploadHttpRequest"]
                        ["uploadUrl"].Value<string>();
                log.LogInformation($"Upload Url: {uploadUrl}");

                //Get the asset URN
                asset = responseJson["value"]["asset"].Value<string>();
                log.LogInformation($"Asset: {asset}");
            }

            //Get the byte array from the rendition
            var memoryStream = new MemoryStream();
            await rendition.Items.FirstOrDefault().GetStreamAsync().Result.CopyToAsync(memoryStream).ConfigureAwait(false);

            ////Download and read the image file content from Content Hub
            //byte[] byteContent;
            //using (var downloadClient = new HttpClient())
            //{
            //    downloadClient.DefaultRequestHeaders.Add("X-Auth-Token", ContentHubAuthToken);
            //    var response = await downloadClient.GetAsync(rendition.Items.FirstOrDefault()?.Href);
            //    byteContent = await response.Content.ReadAsByteArrayAsync();
            //}

            //Upload the image to LinkedIn
            using (var httpClient = new HttpClient())
            {
                using var request = new HttpRequestMessage(new HttpMethod("POST"), uploadUrl);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + LinkedInAuthToken);
                request.Content = new ByteArrayContent(memoryStream.ToArray());
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Created)
                    log.LogInformation($"Image with uploadURL:{uploadUrl} successfully uploaded to LinkedIn");
                else
                    log.LogWarning($"There is some issue uploading image with uploadUrl: {uploadUrl} to LinkedIn. Response details are: {response.Content.ReadAsStringAsync()}");
            }

            //Create LinkedIn post
            using (var postClient = new HttpClient())
            {
                //Post the content
                postClient.DefaultRequestHeaders.Clear();
                postClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LinkedInAuthToken);
                postClient.BaseAddress = new Uri(Constants.BaseLinkedInUrl);

                var postContentWithImageJsonString = $@"{{
                                                        ""author"": ""urn:li:person:{AppSettings.LinkedInPersonId}"",
                                                        ""lifecycleState"": ""PUBLISHED"",
                                                        ""specificContent"": {{
                                                            ""com.linkedin.ugc.ShareContent"": {{
                                                                ""media"": [
                                                                    {{
                                                                        ""media"": ""{asset}"",
                                                                        ""status"": ""READY"",
                                                                        ""title"": {{
                                                                            ""attributes"": [],
                                                                            ""text"": ""{title}""
                                                                        }}
                                                                    }}
                                                                ],
                                                                ""shareCommentary"": {{
                                                                    ""attributes"": [],
                                                                    ""text"": ""{title}""
                                                                }},
                                                                ""shareMediaCategory"": ""IMAGE""
                                                            }}
                                                        }},
                                                        ""visibility"": {{
                                                            ""com.linkedin.ugc.MemberNetworkVisibility"": ""PUBLIC""
                                                        }}
                                                    }}";

                var content = new StringContent(postContentWithImageJsonString, Encoding.UTF8, "application/json");
                var response = await postClient.PostAsync("/v2/ugcPosts", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
        }

        private static async Task PostContent(string title)
        {
            // Call LinkedIn UGC Post API to post the CMP content (without image) from Content Hub
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LinkedInAuthToken);
            client.BaseAddress = new Uri(Constants.BaseLinkedInUrl);

            var postContentJsonString = $@"{{
                                        ""author"": ""urn:li:person:{AppSettings.LinkedInPersonId}"",
                                        ""lifecycleState"": ""PUBLISHED"",
                                        ""specificContent"": {{
                                            ""com.linkedin.ugc.ShareContent"": {{
                                                ""shareCommentary"": {{
                                                    ""attributes"": [],
                                                    ""text"": ""{title}""
                                                }},
                                                ""shareMediaCategory"": ""NONE""
                                            }}
                                        }},
                                        ""visibility"": {{
                                            ""com.linkedin.ugc.MemberNetworkVisibility"": ""PUBLIC""
                                        }}
                                    }}";

            var content = new StringContent(postContentJsonString, Encoding.UTF32, "application/json");
            var response = await client.PostAsync("/v2/ugcPosts", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }
}
