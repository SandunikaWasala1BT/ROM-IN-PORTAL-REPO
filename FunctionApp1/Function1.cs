using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Diagnostics;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Identity.Client;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using static FunctionApp1.Function1;
using Microsoft.Rest;
using Microsoft.Xrm.Sdk.Deployment;

namespace FunctionApp1
{
    public static class Function1
    {
        [FunctionName("DiscoverSMBROM")]
        public static async Task<IActionResult> RunDiscoverSMBROM(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            // Create a Stopwatch instance to measure time
            Stopwatch stopwatch = new Stopwatch();
            try
            {
                //UAT Connection String
                string connectionString = "AuthType=ClientSecret;Url=https://seer-uat.crm11.dynamics.com;ClientId=a4f6bee6-6df1-4630-a717-cc306f482d6f;ClientSecret=sLc8Q~s.LXfB-mI9Xl1YUTvmF5ltcmBaZPWXgbkl";
                var connectionStringV2 =
                   "AuthType=ClientSecret;url=https://designdevpartnerstudio-uk.crm11.dynamics.com/;ClientId=1e559ba5-7a83-4270-a8d9-16b25e3fe902;ClientSecret=M0x8Q~XXDXzwKGy_s2CxC-Tz~DkyQt9FQxezJdj8";

                // Create a service client using the connection string
                var serviceClient = new ServiceClient(connectionString);

                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                dynamic parameters = JsonConvert.DeserializeObject(requestBody);
                Guid accountid = parameters.accountid;
                string reportType = parameters.reportType;

                //setting up parameters
                Guid partnerid;
                Entity entity = serviceClient.Retrieve("account", accountid, new ColumnSet("seer_rateset", "msa_managingpartnerid", "seer_projecttaskset", "seer_outputtype"));
                partnerid = entity.GetAttributeValue<EntityReference>("msa_managingpartnerid").Id;

                Guid languageId;
                languageId = (serviceClient.Retrieve(entity.GetAttributeValue<EntityReference>("seer_outputtype").LogicalName, entity.GetAttributeValue<EntityReference>("seer_outputtype").Id, new ColumnSet("seer_outputlanguage"))).GetAttributeValue<EntityReference>("seer_outputlanguage").Id;

                string outputSetId = Guid.NewGuid().ToString();

                Guid ratesetId;
                QueryExpression query = new QueryExpression("account");
                query.ColumnSet = new ColumnSet("accountid");
                query.Criteria.AddCondition("name", ConditionOperator.Equal, "GYDE365 Master");
                EntityCollection result = serviceClient.RetrieveMultiple(query);

                Guid masterid;
                masterid = result.Entities[0].GetAttributeValue<Guid>("accountid");

                var rateCard = entity.GetAttributeValue<EntityReference>("seer_rateset");
                ratesetId = rateCard.Id;

                Guid projectTaskSetId;
                var projectTask = entity.GetAttributeValue<EntityReference>("seer_projecttaskset");
                projectTaskSetId = projectTask.Id;

                string seerApplications = "";
                var accountapplication = accountapplicationfetchXml(accountid);
                var accountapplicationEntities = RetrieveEntitiesFromFetchXml(serviceClient, accountapplication.accountapplication);
                var query1 = accountapplicationEntities
                    .Where(item => item.Contains("seer_application"))
                    .Select(item =>
                    {
                        EntityReference seerApplicationRef = item.GetAttributeValue<EntityReference>("seer_application");
                        if (seerApplicationRef != null)
                        {
                            string seerApplicationId = seerApplicationRef.Id.ToString();
                            seerApplications = "<value>" + seerApplicationId + "</value>";
                        }
                        return item; // return original item
                    })
                    .ToList(); // Execute the query and materialize results

                string gydeSurveyId = parameters.surveyId;
                //end-setting up parameters

                // Declare fetchXmlQueries outside the loop
                FetchXmlQueries fetchXmlQueries = null;
                fetchXmlQueries = GenerateFetchXml(accountid, partnerid, masterid, seerApplications, languageId, outputSetId, ratesetId, projectTaskSetId);

                string parameterfetchxml = fetchXmlQueries.parameterFetchXML;

                // Call the function to retrieve data using FetchXML

                var parameterEntities = RetrieveEntitiesFromFetchXml(serviceClient, parameterfetchxml);

                //Model data binding
                var parameterModel = TransformParametersEntities(parameterEntities, accountid, serviceClient, reportType);

                //master data
                List<MasterDataModel> masterDataModels = new List<MasterDataModel>();
                using (var _serviceClient = new ServiceClient(connectionStringV2))
                {
                    if (_serviceClient.IsReady)
                    {
                        var gridDataFetch = String.Format(fetchXmlQueries.masterfetchXml, gydeSurveyId);
                        List<Entity> entities = RetrieveEntitiesFromFetchXml(_serviceClient, gridDataFetch);
                        masterDataModels = ExtractMasterDataModel(entities);
                    }
                }

                //project task
                string projecttaskfetchxml = fetchXmlQueries.projectTaskfetchxml;
                var projectTaskEntities = RetrieveEntitiesFromFetchXml(serviceClient, projecttaskfetchxml);
                var extractProjectTaskEntities = ExtractProjectTaskData(projectTaskEntities);

                //List<ProjectTaskDataModel> projectTaskDataModels = new List<ProjectTaskDataModel>();
                //using (var _serviceClient2 = new ServiceClient(connectionString))
                //{
                //    if (_serviceClient2.IsReady)
                //    {
                //        var gridDataFetch = String.Format(fetchXmlQueries.projectTaskfetchxml, gydeSurveyId);
                //        List<Entity> entities = RetrieveEntitiesFromFetchXml(_serviceClient2, gridDataFetch);
                //        projectTaskDataModels = MapProjectTaskDataModel(entities);
                //    }
                //}

                //rate card
                //Guid accountid = Guid.Empty; //parameters.accountid;
                //var serviceClient = new ServiceClient(connectionString);
                //Entity entity = serviceClient.Retrieve("account", accountid, new ColumnSet("seer_rateset", "msa_managingpartnerid", "seer_projecttaskset", "seer_outputtype"));
                //var projectTask = entity.GetAttributeValue<EntityReference>("seer_projecttaskset");

                //var rateCard = entity.GetAttributeValue<EntityReference>("seer_rateset");

                var RelatedConfigurationModel = RelatedConfigurationEntities(rateCard, projectTask);


                //Create a JSON object with properties for combinedEntities and resourceModelData

                //Create a JSON object with properties for combinedEntities and resourceModelData
                var resultObject = new Dictionary<string, object>
                {
                     //{ "OutputSetId", outputSetId },
                     //{ "BaseData", baseData },
                     //{ "OutputData" ,OutputData},
                     //{ "resourceModelData", resourceModelData },
                     //{ "ModuleData", ModuleDataPhase },
                     { "parameterModel", parameterModel.Item1 },
                     //{ "CustomisationModels", CustomisationModels },
                     //{ "CustomRequirmentModel", CustomRequirmentModel },
                     //{ "ProjectTasktModel", extractProjectTaskEntities },
                     //{ "FactorsModel", extractFactorEntities },
                     //{ "PhasesModel", phasesModel },
                     //{ "IntegrationModel", integrationModel },
                     //{ "DataMigrationModel", dataMigrationModel },
                     //{ "DocumentlayoutModel", documentlayoutModel },
                     //{ "licensesModel", parameterModel.Item2 },
                     //{ "UpliftModel", parameterModel.Item3 },
                     //{ "RelatedConfigurationModel", RelatedConfigurationModel },
                     //{ "FitGapDataList", fitGapDataList },
                     //{ "IsvDataList", IsvDataList },
                     { "Executed On ",DateTime.UtcNow },
                     { "MasterDataModel", masterDataModels },
                     { "ProjectTaskDataModel", extractProjectTaskEntities },
                     { "RatecardDataModel", RelatedConfigurationModel },

                };

                return new OkObjectResult(resultObject);
            }
            catch (Exception)
            {

                throw;
            }

        }


        // Define a common function to generate FetchXML queries
        private static FetchXmlQueries GenerateFetchXml(Guid accountid, Guid partnerid, Guid masterid, string application, Guid languageId, string outputsetId, Guid ratesetId, Guid projectTaskSetId)
        {
            FetchXmlQueries fetchXmlQueries = new FetchXmlQueries
            {
                parameterFetchXML = $@"
                    <fetch mapping='logical'>
                      <entity name='seer_romparameters'>
                        <attribute name='seer_account' />
                        <attribute name='seer_changemanager' />
                        <attribute name='seer_changemanagertype' />
                        <attribute name='seer_changemanagertypename' />
                        <attribute name='seer_clouddeploymentmanagement' />
                        <attribute name='seer_clouddeploymentmanagementtype' />
                        <attribute name='seer_clouddeploymentmanagementtypename' />
                        <attribute name='seer_collaterequirements' />
                        <attribute name='seer_collaterequirementstype' />
                        <attribute name='seer_collaterequirementstypename' />
                        <attribute name='seer_conferenceroompilot' />
                        <attribute name='seer_conferenceroompilottype' />
                        <attribute name='seer_conferenceroompilottypename' />
                        <attribute name='seer_datamigration' />
                        <attribute name='seer_datamigrationtype' />
                        <attribute name='seer_datamigrationtypename' />
                        <attribute name='seer_deployprod' />
                        <attribute name='seer_deployprodtype' />
                        <attribute name='seer_deployprodtypename' />
                        <attribute name='seer_deployuat' />
                        <attribute name='seer_deployuattype' />
                        <attribute name='seer_deployuattypename' />
                        <attribute name='seer_designreview' />
                        <attribute name='seer_designreviewtype' />
                        <attribute name='seer_designreviewtypename' />
                        <attribute name='seer_endusertraining' />
                        <attribute name='seer_enablesnapshots' />
                        <attribute name='seer_endusertrainingusers' />
                        <attribute name='seer_formula' />
                        <attribute name='seer_hourlyrate' />
                        <attribute name='seer_hoursperday' />
                        <attribute name='seer_hypercaresupport' />
                        <attribute name='seer_postgolivesupporttype' />
                        <attribute name='seer_postgolivesupporttypename' />
                        <attribute name='seer_programmemanager' />
                        <attribute name='seer_programmemanagertype' />
                        <attribute name='seer_programmemanagertypename' />
                        <attribute name='seer_projectmanagement' />
                        <attribute name='seer_projectmanagementtype' />
                        <attribute name='seer_projectmanagementtypename' />
                        <attribute name='seer_projectsupport' />
                        <attribute name='seer_projectsupporttype' />
                        <attribute name='seer_projectsupporttypename' />
                        <attribute name='seer_reporting' />
                        <attribute name='seer_reportingtype' />
                        <attribute name='seer_reportingtypename' />
                        <attribute name='seer_romcomplexityhigh' />
                        <attribute name='seer_romcomplexitylow' />
                        <attribute name='seer_romcomplexitymedium' />
                        <attribute name='seer_romcomplexitynone' />
                        <attribute name='seer_solutionarchitect' />
                        <attribute name='seer_solutionarchitecttype' />
                        <attribute name='seer_solutionarchitecttypename' />
                        <attribute name='seer_supporthandover' />
                        <attribute name='seer_supporthandovertype' />
                        <attribute name='seer_supporthandovertypename' />
                        <attribute name='seer_technicalarchitect' />
                        <attribute name='seer_technicalarchitecttype' />
                        <attribute name='seer_technicalarchitecttypename' />
                        <attribute name='seer_testing' />
                        <attribute name='seer_testingtype' />
                        <attribute name='seer_testingtypename' />
                        <attribute name='seer_trainthetrainer' />
                        <attribute name='seer_trainthetrainertype' />
                        <attribute name='seer_trainthetrainertypename' />
                        <attribute name='seer_uatsupport' />
                        <attribute name='seer_uatsupporttype' />
                        <attribute name='seer_uatsupporttypename' />
                        <attribute name='seer_name' />
                        <attribute name='transactioncurrencyid' />
                        <attribute name='transactioncurrencyidname' />
                        <attribute name='seer_licencepricesheet' />
                        <attribute name='seer_documentlayoutstype' />
                        <attribute name='seer_documentlayouts' />
                        <attribute name='seer_upliftparameterset' />
                        <attribute name='seer_interfacecomplexityhigh' />
                        <attribute name='seer_interfacecomplexitylow' />
                        <attribute name='seer_interfacecomplexitymedium' />
                        <attribute name='seer_interfacecomplexityveryhigh' />

                        <link-entity name='account' from='seer_romparameters' to='seer_romparametersid' link-type='inner' alias='account'>
                          <attribute name='name' />
                          <filter>
                            <condition attribute='accountid' operator='eq' value='{accountid}' uitype='account' />
                          </filter>
                        </link-entity>
                        <link-entity name='transactioncurrency' from='transactioncurrencyid' to='transactioncurrencyid' alias='currency'>
                          <attribute name='currencysymbol' />
                          <attribute name='isocurrencycode' />
                        </link-entity>
                      </entity>
                    </fetch>",
                masterfetchXml = @"<fetch>
                      <entity name='gyde_workitemresponse'>
                        <attribute name='gyde_module' />
                        <attribute name='gyde_phase' />
                        <attribute name='gyde_resource' />
                        <attribute name='gyde_gapfit' />
                        <attribute name='gyde_moscow' />
                        <attribute name='gyde_survey' />
                        <attribute name='gyde_surveyworkitem' />
                        <attribute name='gyde_complexity' />
                        <attribute name='gyde_workitemresponseid' />
                        <attribute name='gyde_buildestimate' />
                        <attribute name='gyde_estimationmethod' />
                        <filter>
                          <condition attribute='gyde_moscow' operator='in'>
                            <value>267060000</value>
                            <value>267060001</value>
                            <value>267060002</value>
                          </condition>
                        </filter>
                        <link-entity name='gyde_customerbusinesssurvey' from='gyde_gyde365survey' to='gyde_survey' link-type='inner'>
                          <filter>
                            <condition attribute='gyde_customerbusinesssurveyid' operator='eq' value='{0}' uitype='gyde_customerbusinesssurvey' />
                          </filter>
                        </link-entity>
                        <link-entity name='gyde_module' from='gyde_moduleid' to='gyde_module' link-type='inner' alias='module'>
                          <attribute name='gyde_buildestimatehrs' alias='module_buildestimatehrs' />
                        </link-entity>
                      </entity>
                    </fetch>",

                projectTaskfetchxml = $@"<fetch>
                      <entity name='seer_projecttaskoverride'>
                        <attribute name='seer_projecttask' />
                        <attribute name='seer_projecttaskname' />
                        <attribute name='seer_resource' />
                        <attribute name='seer_resourcename' />
                        <attribute name='seer_resourcesecondary' />
                        <attribute name='seer_resourcesecondaryname' />
                        <attribute name='seer_resourcesplit' />
                        <filter>
                          <!--<condition attribute='seer_projecttaskset' operator='eq' value='20983649-ec7e-ee11-8179-000d3a0be042' uiname='PTS: Seer BC ' uitype='seer_projecttaskset' />-->
                          <condition attribute='seer_projecttaskset' operator='eq' value='{projectTaskSetId}' />
                        </filter>
                        <!--<link-entity name='seer_projecttasktranslation' from='seer_projecttaskoverride' to='seer_projecttaskoverrideid' link-type='inner' alias='projecttasktranslation'>
                          <attribute name='seer_comments' />
                          <attribute name='seer_portallanguage' />
                          <attribute name='seer_portallanguagename' />
                          <attribute name='seer_translation' />
                          <filter>
                            <condition attribute='seer_portallanguage' operator='eq' value='{languageId}' />
                          </filter>
                        </link-entity>-->
                        <link-entity name='seer_resource' from='seer_resourceid' to='seer_resource' link-type='outer' alias='primaryresource'>
                          <link-entity name='seer_resourceoverride' from='seer_resource' to='seer_resourceid' link-type='outer' alias='primaryresourceoverride'>
                            <filter>
                              <!--<condition attribute='seer_rateset' operator='eq' value='4cc13b76-6baf-ed11-83ff-002248428331' uiname='Seer BC - Default' uitype='seer_rateset' />-->
                              <condition attribute='seer_rateset' operator='eq' value='{ratesetId}' />
                            </filter>
                            <link-entity name='seer_resourcetranslation' from='seer_resourceoverride' to='seer_resourceoverrideid' link-type='outer' alias='primaryresourcetranslation'>
                              <attribute name='seer_translation' />
                              <filter>
                                <!--<condition attribute='seer_portallanguage' operator='eq' value='50122d0c-87d7-ec11-a7b5-002248008ee0' uiname='English' uitype='adx_portallanguage' />-->
                                <condition attribute='seer_portallanguage' operator='eq' value='{languageId}' />
                              </filter>
                            </link-entity>
                          </link-entity>
                        </link-entity>
                        <link-entity name='seer_resource' from='seer_resourceid' to='seer_resourcesecondary' link-type='outer' alias='secondaryresource'>
                          <link-entity name='seer_resourceoverride' from='seer_resource' to='seer_resourceid' link-type='outer' alias='secondaryresourceoverride'>
                            <filter>
                              <!--<condition attribute='seer_rateset' operator='eq' value='4cc13b76-6baf-ed11-83ff-002248428331' uiname='Seer BC - Default' uitype='seer_rateset' />-->
                              <condition attribute='seer_rateset' operator='eq' value='{ratesetId}' />
                            </filter>
                            <link-entity name='seer_resourcetranslation' from='seer_resourceoverride' to='seer_resourceoverrideid' link-type='outer' alias='secondaryresourcetranslation'>
                              <attribute name='seer_translation' />
                              <filter>
                                <!--<condition attribute='seer_portallanguage' operator='eq' value='50122d0c-87d7-ec11-a7b5-002248008ee0' uiname='English' uitype='adx_portallanguage' />-->
                                <condition attribute='seer_portallanguage' operator='eq' value='{languageId}' />
                              </filter>
                            </link-entity>
                          </link-entity>
                        </link-entity>
                      </entity>
                    </fetch>",
            };

            return fetchXmlQueries;
        }

        // Define a separate function for FetchXML query and data retrieval
        private static List<Entity> RetrieveEntitiesFromFetchXml(ServiceClient serviceClient, string fetchXml)
        {
            var fetchXmlresult = serviceClient.RetrieveMultiple(new FetchExpression(fetchXml));
            return fetchXmlresult.Entities.ToList();
        }

        private static (List<parameterModel>, List<LicenceOutputFetchModel>, List<UplifttypeFetchModel>) TransformParametersEntities(List<Entity> parameterEntities, Guid accountid, ServiceClient serviceClient, string reportType)
        {
            List<parameterModel> parametersModels = new List<parameterModel>();
            List<LicenceOutputFetchModel> licenceOutputModels = new List<LicenceOutputFetchModel>();
            List<UplifttypeFetchModel> uplifttypeOutputModels = new List<UplifttypeFetchModel>();


            EntityReferenceData GetEntityReferenceData(Entity entity, string attributeName)
            {
                EntityReference entityRef = entity.GetAttributeValue<EntityReference>(attributeName);
                if (entityRef != null)
                {
                    return new EntityReferenceData
                    {
                        Id = entityRef.Id,
                        Name = entityRef.Name
                    };
                }
                return null;
            }

            AliasedReferenceData GetAliasedReferenceData(Entity entity, string attributeName)
            {
                AliasedValue aliasedRef = entity.GetAttributeValue<AliasedValue>(attributeName);
                if (aliasedRef != null)
                {
                    return new AliasedReferenceData
                    {
                        AttributeLogicalName = aliasedRef.AttributeLogicalName,
                        EntityLogicalName = aliasedRef.EntityLogicalName,
                        Value = aliasedRef.Value
                    };
                }
                return null;
            }

            parametersModels = parameterEntities.Select(entity =>
            {
                parameterModel paraModel = new parameterModel
                {
                    Account = GetEntityReferenceData(entity, "seer_account"),
                    AccountName = GetAliasedReferenceData(entity, "account.name"),
                    ChangeManager = entity.GetAttributeValue<decimal>("seer_changemanager"),
                    ChangeManagerType = entity.GetAttributeValue<OptionSetValue>("seer_changemanagertype")?.Value ?? 0,
                    CloudDeploymentManagement = entity.GetAttributeValue<decimal>("seer_clouddeploymentmanagement"),
                    CloudDeploymentManagementType = entity.GetAttributeValue<OptionSetValue>("seer_clouddeploymentmanagementtype")?.Value ?? 0,
                    Documentlayoutstype = entity.GetAttributeValue<OptionSetValue>("seer_documentlayoutstype")?.Value ?? 0,
                    Documentlayouts = entity.GetAttributeValue<decimal>("seer_documentlayouts"),
                    CollateRequirment = entity.GetAttributeValue<decimal>("seer_collaterequirements"),
                    CollateRequirmentType = entity.GetAttributeValue<OptionSetValue>("seer_collaterequirementstype")?.Value ?? 0,
                    ConferenceRoomPilot = entity.GetAttributeValue<decimal>("seer_conferenceroompilot"),
                    ConferenceRoomPilotType = entity.GetAttributeValue<OptionSetValue>("seer_conferenceroompilottype")?.Value ?? 0,
                    Currency = GetEntityReferenceData(entity, "transactioncurrencyid"),
                    Seer_Enablesnapshots = entity.GetAttributeValue<bool>("seer_enablesnapshots"),
                    Currencysymbol = GetAliasedReferenceData(entity, "currency.currencysymbol"),
                    Isocurrencycode = GetAliasedReferenceData(entity, "currency.isocurrencycode"),
                    DataMigration = entity.GetAttributeValue<decimal>("seer_datamigration"),
                    DataMigrationType = entity.GetAttributeValue<OptionSetValue>("seer_datamigrationtype")?.Value ?? 0,
                    DeployProd = entity.GetAttributeValue<decimal>("seer_deployprod"),
                    DeployProdType = entity.GetAttributeValue<OptionSetValue>("seer_deployprodtype")?.Value ?? 0,
                    DeployUat = entity.GetAttributeValue<decimal>("seer_deployuat"),
                    DeployUatType = entity.GetAttributeValue<OptionSetValue>("seer_deployuattype")?.Value ?? 0,
                    DesignReview = entity.GetAttributeValue<decimal>("seer_designreview"),
                    DesignReviewType = entity.GetAttributeValue<OptionSetValue>("seer_designreviewtype")?.Value ?? 0,
                    EndUserTraining = entity.GetAttributeValue<decimal>("seer_endusertraining"),
                    EndUserTrainingUsers = entity.GetAttributeValue<decimal>("seer_endusertrainingusers"),
                    HourlyRate = entity.GetAttributeValue<Money>("seer_hourlyrate"),
                    HoursPerday = entity.GetAttributeValue<decimal>("seer_hoursperday"),
                    LicencePriceSheet = GetEntityReferenceData(entity, "seer_licencepricesheet"),
                    ParameterSetName = entity.GetAttributeValue<string>("seer_name"),
                    ParametersId = entity.GetAttributeValue<Guid>("seer_romparametersid"),
                    PostGoLiveSupport = entity.GetAttributeValue<decimal>("seer_hypercaresupport"),
                    PostGoLiveSupportType = entity.GetAttributeValue<OptionSetValue>("seer_postgolivesupporttype")?.Value ?? 0,
                    ProjectManagement = entity.GetAttributeValue<decimal>("seer_projectmanagement"),
                    ProjectManagementType = entity.GetAttributeValue<OptionSetValue>("seer_projectmanagementtype")?.Value ?? 0,
                    ProgrammeManager = entity.GetAttributeValue<decimal>("seer_programmemanager"),
                    ProgrammeManagerType = entity.GetAttributeValue<OptionSetValue>("seer_programmemanagertype")?.Value ?? 0,
                    ProjectSupport = entity.GetAttributeValue<decimal>("seer_projectsupport"),
                    ProjectSupportType = entity.GetAttributeValue<OptionSetValue>("seer_projectsupporttype")?.Value ?? 0,
                    Reporting = entity.GetAttributeValue<decimal>("seer_reporting"),
                    ReportingType = entity.GetAttributeValue<OptionSetValue>("seer_reportingtype")?.Value ?? 0,
                    RiskFormula = entity.GetAttributeValue<string>("seer_formula"),
                    RomComplexityHigh = entity.GetAttributeValue<int>("seer_romcomplexityhigh"),
                    RomComplexityLow = entity.GetAttributeValue<int>("seer_romcomplexitylow"),
                    RomComplexityMedium = entity.GetAttributeValue<int>("seer_romcomplexitymedium"),
                    RomComplexityNone = entity.GetAttributeValue<int>("seer_romcomplexitynone"),
                    SolutionArchitecture = entity.GetAttributeValue<decimal>("seer_solutionarchitect"),
                    SolutionArchitectureType = entity.GetAttributeValue<OptionSetValue>("seer_solutionarchitecttype")?.Value ?? 0,
                    SupportHandOver = entity.GetAttributeValue<decimal>("seer_supporthandover"),
                    SupportHandOverType = entity.GetAttributeValue<OptionSetValue>("seer_supporthandovertype")?.Value ?? 0,
                    TechnicalArchitect = entity.GetAttributeValue<decimal>("seer_technicalarchitect"),
                    TechnicalArchitectType = entity.GetAttributeValue<OptionSetValue>("seer_technicalarchitecttype")?.Value ?? 0,
                    Testing = entity.GetAttributeValue<decimal>("seer_testing"),
                    TestingType = entity.GetAttributeValue<OptionSetValue>("seer_testingtype")?.Value ?? 0,
                    TrainTheTrainer = entity.GetAttributeValue<decimal>("seer_trainthetrainer"),
                    TrainTheTrainerType = entity.GetAttributeValue<OptionSetValue>("seer_trainthetrainertype")?.Value ?? 0,
                    UatSupport = entity.GetAttributeValue<decimal>("seer_uatsupport"),
                    UatSupportType = entity.GetAttributeValue<OptionSetValue>("seer_uatsupporttype")?.Value ?? 0,
                    Upliftparameterset = GetEntityReferenceData(entity, "seer_upliftparameterset"),
                    seer_interfacecomplexityhigh = entity.GetAttributeValue<decimal>("seer_interfacecomplexityhigh"),
                    seer_interfacecomplexitylow = entity.GetAttributeValue<decimal>("seer_interfacecomplexitylow"),
                    seer_interfacecomplexitymedium = entity.GetAttributeValue<decimal>("seer_interfacecomplexitymedium"),
                    seer_interfacecomplexityveryhigh = entity.GetAttributeValue<decimal>("seer_interfacecomplexityveryhigh"),
                };

                // Fetch the licence price sheet data using the generated queries
                var licencePriceSheetQueries = licencePriceSheetfetchXml(accountid, paraModel.Currency.Id, paraModel.LicencePriceSheet.Id);
                var licencePriceSheetEntities = RetrieveEntitiesFromFetchXml(serviceClient, licencePriceSheetQueries.licencePriceSheetfetchXml);

                // Calculate the totalLicenceCount and set it in the parameterModel
                paraModel.TotalLicenceCount = licencePriceSheetEntities
                    .Where(entity => entity.Attributes.ContainsKey("seer_licencecount"))  // Check if the attribute exists
                    .Sum(entity => (int)entity.Attributes["seer_licencecount"]);


                return paraModel;
            }).ToList();

            // Fetch licence price sheet data for the first parameter model if any exists
            if (parametersModels.Any())
            {
                var firstParameterModel = parametersModels.First();
                var licencePriceSheetQueries = licencePriceSheetfetchXml(accountid, firstParameterModel.Currency.Id, firstParameterModel.LicencePriceSheet.Id);
                var licencePriceSheetEntities = RetrieveEntitiesFromFetchXml(serviceClient, licencePriceSheetQueries.licencePriceSheetfetchXml);

                // Map licencePriceSheetEntities to LicenceOutputFetchModel
                licenceOutputModels = licencePriceSheetEntities.Select(entity =>
                {
                    return new LicenceOutputFetchModel
                    {
                        LicenceCount = entity.GetAttributeValue<int>("seer_licencecount"),
                        MicrosoftLicenseBillingPeriod = GetAliasedReferenceData(entity, "microsoftlicense.seer_billingperiod"),
                        LicencePriceCost = GetAliasedReferenceData(entity, "licensepricing.seer_cost"),
                        LicencePriceSell = GetAliasedReferenceData(entity, "licensepricing.seer_sell"),
                        SeerCurrency = GetAliasedReferenceData(entity, "licensepricing.seer_currency"),
                        MicrosoftlicenseName = (string)entity.GetAttributeValue<AliasedValue>("microsoftlicense.seer_name")?.Value,
                        SeerSource = entity.GetAttributeValue<string>("seer_source"),

                    };
                }).ToList();
            }


            if (reportType == "381070001")
            {
                if (parametersModels.Any())
                {
                    var firstParameterModel = parametersModels.First();

                    if (firstParameterModel.Upliftparameterset != null)
                    {

                        var uplifttypefetchQueries = uplifttypefetchXml(firstParameterModel.Upliftparameterset.Id);
                        var uplifttypeSheetEntities = RetrieveEntitiesFromFetchXml(serviceClient, uplifttypefetchQueries.uplifttypefetchXml);

                        uplifttypeOutputModels = uplifttypeSheetEntities.Select(entity =>
                        {
                            return new UplifttypeFetchModel
                            {
                                seer_appliesto = entity.GetAttributeValue<OptionSetValue>("seer_appliesto")?.Value ?? 0,
                                seer_uplifttype = entity.GetAttributeValue<OptionSetValue>("seer_uplifttype")?.Value ?? 0,
                                seer_upliftpercent = GetAliasedReferenceData(entity, "uplift_params.seer_upliftpercent"),
                                seer_upperlimit = GetAliasedReferenceData(entity, "uplift_params.seer_upperlimit"),
                                seer_uplifttypeid = entity.GetAttributeValue<Guid>("seer_uplifttypeid"),

                            };
                        }).ToList();

                    }
                }
            }


            return (parametersModels, licenceOutputModels, uplifttypeOutputModels); ;
        }

        private static FetchXmlQueries licencePriceSheetfetchXml(Guid accountid, Guid transactioncurrencyid, Guid licencepricesheet)
        {
            FetchXmlQueries fetchXmlQueries = new FetchXmlQueries
            {
                licencePriceSheetfetchXml = $@"<fetch>
                      <entity name='seer_licenceoutput'>
                        <attribute name='seer_licenceguideversion' />
                        <attribute name='seer_licenceoutputid' />
                        <attribute name='seer_calculationid' />
                        <attribute name='seer_source' />
                        <attribute name='seer_licencecount' />
                        <attribute name='seer_account' />
                        <attribute name='seer_name' />
                        <attribute name='seer_licencecalculatortype' />
                        <filter>
                          <!--<condition attribute='seer_account' operator='eq' value='adcf1813-7430-ee11-bdf3-000d3a0be042' uiname='Multi Company Test' uitype='account' />-->
                          <condition attribute='seer_account' operator='eq' value='{accountid}' uiname='ERP SMB v2.2' uitype='account' />
                        </filter>
                        <link-entity name='seer_microsoftlicence' from='seer_microsoftlicenceid' to='seer_microsoftlicence' link-type='inner' alias='microsoftlicense'>
                          <attribute name='seer_name' />
                          <attribute name='seer_billingperiod' />
                          <attribute name='seer_billingperiodname' />
                          <attribute name='seer_licencetype' />
                          <attribute name='seer_vendor' />
                          <link-entity name='seer_licencepricing' from='seer_licence' to='seer_microsoftlicenceid' link-type='inner' alias='licensepricing'>
                            <attribute name='seer_licencepriceid' />
                            <attribute name='seer_pricesheet' />
                            <attribute name='seer_cost' />
                            <attribute name='seer_licencepricingid' />
                            <attribute name='seer_sell' />
                            <attribute name='seer_licence' />
                            <attribute name='seer_account' />
                            <attribute name='seer_currency' />
                            <filter>
                              <!--<condition attribute='seer_currency' operator='eq' value='4171fc12-5ec6-eb11-8235-000d3ad5fbfe' uiname='British Pound' uitype='transactioncurrency' />-->
                              <condition attribute='seer_currency' operator='eq' value='{transactioncurrencyid}' uiname='British Pound' uitype='transactioncurrency' />
                            </filter>
                            <link-entity name='seer_licencepricesheet' from='seer_licencepricesheetid' to='seer_pricesheet' alias='pricesheet'>
                              <filter>
                                <!--<condition attribute='seer_licencepricesheetid' operator='eq' value='81888052-4ee3-ed11-8846-000d3a0be042' uiname='Seer BC 2' uitype='seer_licencepricesheet' />-->
                                <condition attribute='seer_licencepricesheetid' operator='eq' value='{licencepricesheet}' />
                              </filter>
                            </link-entity>
                          </link-entity>
                        </link-entity>
                      </entity>
                    </fetch>"
            };

            return fetchXmlQueries;
        }

        private static FetchXmlQueries uplifttypefetchXml(Guid upliftparameterset)
        {
            FetchXmlQueries fetchXmlQueries = new FetchXmlQueries
            {
                uplifttypefetchXml = $@"<fetch>
                      <entity name='seer_uplifttype'>
                        <attribute name='seer_appliesto' />
                        <attribute name='seer_appliestoname' />
                        <attribute name='seer_uplifttype' />
                        <attribute name='seer_uplifttypename' />
                        <order attribute='seer_uplifttype' />
                        <link-entity name='seer_upliftparameters' from='seer_uplifttype' to='seer_uplifttypeid' link-type='inner' alias='uplift_params'>
                          <attribute name='seer_upliftpercent' />
                          <attribute name='seer_upperlimit' />
                          <order attribute='seer_upperlimit' />
                          <link-entity name='seer_upliftparameterset' from='seer_upliftparametersetid' to='seer_upliftparameterset' link-type='inner' alias='uplift_param_set'>
                            <filter>
                              <!--<condition attribute='seer_upliftparametersetid' operator='eq' value='8e1d5608-455c-ee11-8def-002248015232' />-->
                              <condition attribute='seer_upliftparametersetid' operator='eq' value='{upliftparameterset}' />
                            </filter>
                          </link-entity>
                        </link-entity>
                      </entity>
                    </fetch>"
            };

            return fetchXmlQueries;
        }

        private static FetchXmlQueries accountapplicationfetchXml(Guid accountid)
        {
            FetchXmlQueries fetchXmlQueries = new FetchXmlQueries
            {
                accountapplication = $@"
                    <fetch>
                      <entity name='seer_accountapplication'>
                        <attribute name='seer_application' />
                        <attribute name='seer_account' />
                        <attribute name='seer_output' />
                        <filter>
                          <condition attribute='seer_account' operator='eq' value='{accountid}' />
                          <condition attribute='seer_output' operator='eq' value='1' />
                        </filter>
                      </entity>
                    </fetch>"
            };
            return fetchXmlQueries;
        }

        static EntityReferenceData GetEntityReferenceData(Entity entity, string attributeName)
        {
            EntityReference entityRef = entity.GetAttributeValue<EntityReference>(attributeName);
            if (entityRef != null)
            {
                return new EntityReferenceData
                {
                    Id = entityRef.Id,
                    Name = entityRef.Name
                };
            }
            return null;
        }

        static AliasedReferenceData GetAliasedReferenceData(Entity entity, string attributeName)
        {
            AliasedValue aliasedRef = entity.GetAttributeValue<AliasedValue>(attributeName);

            var af = entity.GetAttributeValue<OptionSetValue>("gyde_moscow");
            if (aliasedRef != null)
            {
                return new AliasedReferenceData
                {
                    AttributeLogicalName = aliasedRef.AttributeLogicalName,
                    EntityLogicalName = aliasedRef.EntityLogicalName,
                    Value = aliasedRef.Value
                };
            }
            return null;
        }

        static OptionsetReferenceData GetOptionsetReferenceData(Entity entity, string attributeName)
        {
            var name = entity.FormattedValues[attributeName].ToString();
            var value = entity.GetAttributeValue<OptionSetValue>(attributeName)?.Value ?? 0;
            if (entity.GetAttributeValue<OptionSetValue>(attributeName) is not null)
            {
                return new OptionsetReferenceData
                {
                    Name = name,
                    Value = value,
                };
            }

            return null;
        }

        private static List<RelatedConfigurationModel> RelatedConfigurationEntities(EntityReference RateCard, EntityReference Projecttask)
        {
            List<RelatedConfigurationModel> relatedConfigurationEntities = new List<RelatedConfigurationModel>();

            RelatedConfigurationModel relatedConfigurationModel = new RelatedConfigurationModel
            {
                RateCardId = RateCard.Id,
                RateCardName = RateCard.Name,
                ProjectTaskId = Projecttask.Id,
                ProjectTaskName = Projecttask.Name,
            };

            relatedConfigurationEntities.Add(relatedConfigurationModel);

            return relatedConfigurationEntities;
        }

        private static List<MasterDataModel> ExtractMasterDataModel(List<Entity> entities)
        {
            List<MasterDataModel> masterDataModels = new List<MasterDataModel>();

            foreach (var entity in entities)
            {
                MasterDataModel masterDataModel = new MasterDataModel
                {
                    Module = entity.GetAttributeValue<EntityReference>("gyde_module") != null && entity.GetAttributeValue<EntityReference>("gyde_module").Id != Guid.Empty ?
                             GetEntityReferenceData(entity, "gyde_module") : null,
                    Phase = GetOptionsetReferenceData(entity, "gyde_phase"),
                    Resource = GetEntityReferenceData(entity, "gyde_resource"),
                    FitGap = GetOptionsetReferenceData(entity, "gyde_gapfit"),
                    //SeerMoscow = GetAliasedReferenceData(entity, "gyde_moscow"),
                    SeerMoscow = GetOptionsetReferenceData(entity, "gyde_moscow"),
                    Survey = entity.GetAttributeValue<EntityReference>("gyde_survey") != null && entity.GetAttributeValue<EntityReference>("gyde_survey").Id != Guid.Empty ?
                             GetEntityReferenceData(entity, "gyde_survey") : null,
                    SurveyWorkItem = entity.GetAttributeValue<EntityReference>("gyde_surveyworkitem") != null && entity.GetAttributeValue<EntityReference>("gyde_surveyworkitem").Id != Guid.Empty ? GetEntityReferenceData(entity, "gyde_surveyworkitem") : null,
                    Complexity = GetOptionsetReferenceData(entity, "gyde_complexity"),
                    WorkItemResponseId = entity.GetAttributeValue<Guid>("gyde_workitemresponseid"),
                    Quantity = 1, //default value set as 1
                    BuildEstimate = entity.GetAttributeValue<decimal>("gyde_buildestimate"),
                    EstimationMethod = GetOptionsetReferenceData(entity, "gyde_estimationmethod"),
                    ModuleBuildEstimateHrs = entity.GetAttributeValue<decimal>("module_buildestimatehrs"),

                };

                masterDataModels.Add(masterDataModel);

            }

            return masterDataModels;
        }

        private static List<ProjectTaskEntity> ExtractProjectTaskData(List<Entity> projectTaskEntities)
        {
            var projectTaskDataList = new List<ProjectTaskEntity>();


            EntityReferenceData GetEntityReferenceData(Entity entity, string attributeName)
            {
                EntityReference entityRef = entity.GetAttributeValue<EntityReference>(attributeName);
                if (entityRef != null)
                {
                    return new EntityReferenceData
                    {
                        Id = entityRef.Id,
                        Name = entityRef.Name
                    };
                }
                return null;
            }

            AliasedReferenceData GetAliasedReferenceData(Entity entity, string attributeName)
            {
                AliasedValue aliasedRef = entity.GetAttributeValue<AliasedValue>(attributeName);
                if (aliasedRef != null)
                {
                    return new AliasedReferenceData
                    {
                        AttributeLogicalName = aliasedRef.AttributeLogicalName,
                        EntityLogicalName = aliasedRef.EntityLogicalName,
                        Value = aliasedRef.Value
                    };
                }
                return null;
            }
            foreach (var entity in projectTaskEntities)
            {
                var projectTaskData = new ProjectTaskEntity
                {
                    Name = GetEntityReferenceData(entity, "seer_projecttask").Name,
                    ProjectTaskCustomer_Name = (string)entity.GetAttributeValue<AliasedValue>("project_task_customer.seer_name")?.Value,
                    ProjectTaskCustomer_Resource = GetEntityReferenceData(entity, "seer_resource"),
                    ProjectTaskCustomer_ResourceSecondary = GetEntityReferenceData(entity, "seer_resourcesecondary"),
                    ProjectTaskCustomer_ResourceSecondaryName = (string)entity.GetAttributeValue<AliasedValue>("project_task_customer.seer_resourcesecondaryname")?.Value,
                    ProjectTaskCustomer_ResourceSplit = entity.GetAttributeValue<double>("seer_resourcesplit"),
                    Customer_Name = (string)entity.GetAttributeValue<AliasedValue>("customer.seer_name")?.Value,
                    Customer_Resource = GetEntityReferenceData(entity, "customer.seer_resource"),
                    Customer_ResourceName = (string)entity.GetAttributeValue<AliasedValue>("customer.seer_resourcename")?.Value,
                    Customer2_Name = (string)entity.GetAttributeValue<AliasedValue>("customer2.seer_name")?.Value,
                    Customer2_Resource = GetEntityReferenceData(entity, "customer2.seer_resource"),
                    Customer2_ResourceName = (string)entity.GetAttributeValue<AliasedValue>("customer2.seer_resourcename")?.Value,
                    ProjectTaskPartner_Name = (string)entity.GetAttributeValue<AliasedValue>("project_task_partner.seer_name")?.Value,
                    ProjectTaskPartner_Resource = GetEntityReferenceData(entity, "project_task_partner.seer_resource"),
                    ProjectTaskPartner_ResourceSecondary = GetEntityReferenceData(entity, "project_task_partner.seer_resourcesecondary"),
                    ProjectTaskPartner_ResourceSplit = (double?)entity.GetAttributeValue<AliasedValue>("project_task_partner.seer_resourcesplit")?.Value ?? 0,
                    Partner_Name = (string)entity.GetAttributeValue<AliasedValue>("partner.seer_name")?.Value,
                    Partner_Resource = GetEntityReferenceData(entity, "partner.seer_resource"),
                    Partner_ResourceName = (string)entity.GetAttributeValue<AliasedValue>("partner.seer_resourcename")?.Value,
                    Partner2_Name = (string)entity.GetAttributeValue<AliasedValue>("partner2.seer_name")?.Value,
                    Partner2_Resource = GetEntityReferenceData(entity, "partner2.seer_resource"),
                    Partner2_ResourceName = (string)entity.GetAttributeValue<AliasedValue>("partner2.seer_resourcename")?.Value,
                    SeerProjectTask = GetEntityReferenceData(entity, "seer_projecttask"),
                    SeerProjectTaskName = (string)entity.GetAttributeValue<AliasedValue>("seer_projecttaskname")?.Value,
                    SeerResource = GetEntityReferenceData(entity, "seer_resource"),
                    SeerResourceName = (string)entity.GetAttributeValue<AliasedValue>("seer_resourcename")?.Value,
                    SeerResourceSecondary = GetEntityReferenceData(entity, "seer_resourcesecondary"),
                    SeerResourceSecondaryName = (string)entity.GetAttributeValue<AliasedValue>("seer_resourcesecondaryname")?.Value,
                    SeerResourceSplit = entity.GetAttributeValue<double>("seer_resourcesplit"),
                    SeerTranslationPrimary = (string)entity.GetAttributeValue<AliasedValue>("primaryresourcetranslation.seer_translation")?.Value,
                    SeerTranslationSecondary = (string)entity.GetAttributeValue<AliasedValue>("secondaryresourcetranslation.seer_translation")?.Value,
                    ProjectTaskOverideId = entity.GetAttributeValue<Guid>("seer_projecttaskoverrideid"),
                };
                projectTaskDataList.Add(projectTaskData);
            }
            return projectTaskDataList;
        }
        //private static List<ProjectTaskDataModel> MapProjectTaskDataModel(List<Entity> entities)
        //{
        //    List<ProjectTaskDataModel> projectTaskDataModels = new List<ProjectTaskDataModel>();

        //    foreach (var entity in entities)
        //    {
        //        ProjectTaskDataModel projectTaskDataModel = new ProjectTaskDataModel
        //        {
        //            ProjectTask = entity.GetAttributeValue<EntityReference>("seer_projecttask").Name,
        //            Resource = entity.GetAttributeValue<EntityReference>("seer_resource").Name,
        //            ResourceName = entity.GetAttributeValue<string>("seer_resource"),
        //            ProjectTaskName = entity.GetAttributeValue<string>("seer_projecttask"),
        //            Translation = entity.GetAttributeValue<string>("primaryresourcetranslation.seer_translation"),
        //            ResourceSplit = entity.GetAttributeValue<string>("seer_resourcesplit"),
        //            //ResourceSecondary = entity.GetAttributeValue<string>("gyde_buildestimate"),
        //            //ResourceSecondaryName = entity.GetAttributeValue<string>("gyde_buildestimate"),              
        //        };

        //        projectTaskDataModels.Add(projectTaskDataModel);

        //    }

        //    return projectTaskDataModels;
        //}


        //custom class for FetchXML queries
        public class FetchXmlQueries
        {
            public string parameterFetchXML { get; set; }
            public string uplifttypefetchXml { get; set; }
            public string licencePriceSheetfetchXml { get; set; }
            public string masterfetchXml { get; set; }
            public string projectTaskfetchxml { get; set; }
            public string accountapplication { get; set; }
        }

        public class parameterModel
        {
            public EntityReferenceData Account { get; set; }
            public AliasedReferenceData AccountName { get; set; }
            public decimal ChangeManager { get; set; }
            public int ChangeManagerType { get; set; }
            public decimal CloudDeploymentManagement { get; set; }
            public int CloudDeploymentManagementType { get; set; }
            public decimal Documentlayouts { get; set; }
            public int Documentlayoutstype { get; set; }
            public decimal CollateRequirment { get; set; }
            public int CollateRequirmentType { get; set; }
            public decimal ConferenceRoomPilot { get; set; }
            public int ConferenceRoomPilotType { get; set; }
            //public EntityReferenceData CreatedBy { get; set; }
            //public EntityReferenceData CreatedByDelegate { get; set; }
            //public EntityReferenceData CreatedByPortal { get; set; }
            //public DateTime CreatedOn { get; set; }
            public EntityReferenceData Currency { get; set; }
            public bool Seer_Enablesnapshots { get; set; }
            public AliasedReferenceData Currencysymbol { get; set; }
            public AliasedReferenceData Isocurrencycode { get; set; }
            public decimal DataMigration { get; set; }
            public int DataMigrationType { get; set; }
            public decimal DeployProd { get; set; }
            public int DeployProdType { get; set; }
            public decimal DeployUat { get; set; }
            public int DeployUatType { get; set; }
            public decimal DesignReview { get; set; }
            public int DesignReviewType { get; set; }
            public decimal EndUserTraining { get; set; }
            public decimal EndUserTrainingUsers { get; set; }
            //public decimal ExchangeRate { get; set; }
            public Money HourlyRate { get; set; }
            //public decimal HourlyRateBase { get; set; }
            public decimal HoursPerday { get; set; }
            //public string ImportSequenceNumber { get; set; }
            //public EntityReferenceData LicencePriceSheet { get; set; }
            //public EntityReferenceData ModifiedBy { get; set; }
            //public EntityReferenceData ModifiedByDelegate { get; set; }
            //public EntityReferenceData ModifiedByPortal { get; set; }
            //public DateTime ModifiedOn { get; set; }
            //public string Owner { get; set; }
            //public EntityReferenceData OwningbusinessUnit { get; set; }
            //public EntityReferenceData OwningTeam { get; set; }
            //public EntityReferenceData OwningUser { get; set; }
            public string ParameterSetName { get; set; }
            public Guid ParametersId { get; set; }
            public EntityReferenceData LicencePriceSheet { get; set; }
            public decimal PostGoLiveSupport { get; set; }
            public int PostGoLiveSupportType { get; set; }
            public decimal ProjectManagement { get; set; }
            public int ProjectManagementType { get; set; }
            public decimal ProgrammeManager { get; set; }
            public int ProgrammeManagerType { get; set; }
            public decimal ProjectSupport { get; set; }
            public int ProjectSupportType { get; set; }
            //public DateOnly RecordCreatedOn { get; set; }
            public decimal Reporting { get; set; }
            public int ReportingType { get; set; }
            public string RiskFormula { get; set; }
            public int RomComplexityHigh { get; set; }
            public int RomComplexityLow { get; set; }
            public int RomComplexityMedium { get; set; }
            public int RomComplexityNone { get; set; }
            //public string RomParameters { get; set; }
            public decimal SolutionArchitecture { get; set; }
            public int SolutionArchitectureType { get; set; }
            //public string Status { get; set; }
            //public string StatusReason { get; set; }
            public decimal SupportHandOver { get; set; }
            public int SupportHandOverType { get; set; }
            public decimal TechnicalArchitect { get; set; }
            public int TechnicalArchitectType { get; set; }
            public decimal Testing { get; set; }
            public int TestingType { get; set; }
            //public int TimeZoneRuleVersionNumber { get; set; }
            public decimal TrainTheTrainer { get; set; }
            public int TrainTheTrainerType { get; set; }
            public decimal UatSupport { get; set; }
            public int UatSupportType { get; set; }
            public int TotalLicenceCount { get; set; }
            public EntityReferenceData Upliftparameterset { get; internal set; }
            public decimal seer_interfacecomplexityhigh { get; internal set; }
            public decimal seer_interfacecomplexitylow { get; internal set; }
            public decimal seer_interfacecomplexitymedium { get; internal set; }
            public decimal seer_interfacecomplexityveryhigh { get; internal set; }

            //public EntityReferenceData UpliftParameterSet { get; set; }
            //public int UtcConversionTimeZoneCode { get; set; }
            //public int VersionNumber { get; set; }
        }

        public class EntityReferenceData
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
        }
        public class AliasedReferenceData
        {
            public string AttributeLogicalName { get; set; }
            public string EntityLogicalName { get; set; }
            public Object Value { get; set; }
        }
        public class LicenceOutputFetchModel
        {
            public int LicenceCount { get; set; }
            public AliasedReferenceData MicrosoftLicenseBillingPeriod { get; set; }
            public AliasedReferenceData LicencePriceCost { get; set; }
            public AliasedReferenceData LicencePriceSell { get; set; }
            public AliasedReferenceData SeerCurrency { get; set; }
            public string MicrosoftlicenseName { get; set; }
            public string SeerSource { get; internal set; }
        }
        public class UplifttypeFetchModel
        {
            public int seer_appliesto { get; set; }
            public int seer_uplifttype { get; set; }
            public AliasedReferenceData seer_upliftpercent { get; set; }
            public AliasedReferenceData seer_upperlimit { get; set; }
            public Guid seer_uplifttypeid { get; set; }
        }
        public class OptionsetReferenceData
        {
            public string Name { get; set; }
            public object Value { get; set; }
        }
        public class MasterDataModel
        {
            public string Application { get; set; }
            public OptionsetReferenceData Complexity { get; set; }
            public OptionsetReferenceData FitGap { get; set; }
            public EntityReferenceData Module { get; set; }
            public EntityReferenceData Resource { get; set; }
            public string SecondaryResource { get; set; }
            public string ResourceSplit { get; set; }
            public OptionsetReferenceData Phase { get; set; }
            public EntityReferenceData Survey { get; set; }
            public EntityReferenceData SurveyWorkItem { get; set; }
            public string Licence { get; set; }
            public string BaseEstimate { get; set; }
            public string DesignEstimate { get; set; }
            public decimal BuildEstimate { get; set; }
            public string BasePoints { get; set; }
            public string DesignPoints { get; set; }
            public string BuildPoints { get; set; }
            public string Requirement { get; set; }
            public string ISV_IP { get; set; }
            public string BillingMethod { get; set; }
            public int Quantity { get; set; }
            public OptionsetReferenceData SeerMoscow { get; set; }
            public string ModuleTranslation { get; set; }
            public Guid WorkItemResponseId { get; set; }
            //public string Quantity { get; set; }
            public OptionsetReferenceData EstimationMethod { get; set; }
            public decimal ModuleBuildEstimateHrs { get; set; }
        }
        public class ProjectTaskDataModel
        {
            public EntityReferenceData ProjectTask { get; set; }
            public string ProjectTaskName { get; set; }
            public EntityReferenceData Resource { get; set; }
            public string ResourceName { get; set; }
            public string ResourceSecondary { get; set; }
            public EntityReferenceData ResourceSecondaryName { get; set; }
            public double ResourceSplit { get; set; }
            public string Translation { get; set; }
            //public int Translation { get; set; }

        }
        public class RelatedConfigurationModel
        {
            public Guid RateCardId { get; internal set; }
            public string RateCardName { get; internal set; }
            public Guid ProjectTaskId { get; internal set; }
            public string ProjectTaskName { get; internal set; }
        }
        public class ProjectTaskEntity
        {
            public string Name { get; set; }
            public string ProjectTaskCustomer_Name { get; set; }
            public EntityReferenceData ProjectTaskCustomer_Resource { get; set; }
            public EntityReferenceData ProjectTaskCustomer_ResourceSecondary { get; set; }
            public string ProjectTaskCustomer_ResourceSecondaryName { get; set; }
            public double ProjectTaskCustomer_ResourceSplit { get; set; }
            public string Customer_Name { get; set; }
            public EntityReferenceData Customer_Resource { get; set; }
            public string Customer_ResourceName { get; set; }
            public string Customer2_Name { get; set; }
            public EntityReferenceData Customer2_Resource { get; set; }
            public string Customer2_ResourceName { get; set; }
            public string ProjectTaskPartner_Name { get; set; }
            public EntityReferenceData ProjectTaskPartner_Resource { get; set; }
            public EntityReferenceData ProjectTaskPartner_ResourceSecondary { get; set; }
            public double ProjectTaskPartner_ResourceSplit { get; set; }
            public string Partner_Name { get; set; }
            public EntityReferenceData Partner_Resource { get; set; }
            public string Partner_ResourceName { get; set; }
            public string Partner2_Name { get; set; }
            public EntityReferenceData Partner2_Resource { get; set; }
            public string Partner2_ResourceName { get; set; }

            ////////////////////////////////////////////////////////////////
            public EntityReferenceData SeerProjectTask { get; set; }
            public string SeerProjectTaskName { get; set; }
            public EntityReferenceData SeerResource { get; set; }
            public string SeerResourceName { get; set; }
            public EntityReferenceData SeerResourceSecondary { get; set; }
            public string SeerResourceSecondaryName { get; set; }
            public double SeerResourceSplit { get; set; }

            // Linked entity - seer_projecttasktranslation
            public string SeerComments { get; set; }
            public AliasedReferenceData SeerPortalLanguage { get; set; }
            public string SeerPortalLanguageName { get; set; }
            public string SeerTranslation { get; set; }

            // Linked entity - seer_resourcetranslation (outer link)
            public string SeerTranslationPrimary { get; set; }

            // Linked entity - seer_resourcetranslation (outer link for secondary resource)
            public string SeerTranslationSecondary { get; set; }
            public Guid ProjectTaskOverideId { get; set; }
        }
    }
}
