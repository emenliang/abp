using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Http;
using Volo.Abp.Http.Modeling;
using Volo.Abp.Reflection;

namespace Volo.Abp.AspNetCore.Mvc
{
    public class AbpAppServiceConvention : IAbpAppServiceConvention, ITransientDependency
    {
        private readonly AbpAspNetCoreMvcOptions _options;

        public AbpAppServiceConvention(IOptions<AbpAspNetCoreMvcOptions> options)
        {
            _options = options.Value;
        }

        public void Apply(ApplicationModel application)
        {
            ApplyForControllers(application);
        }

        protected virtual void ApplyForControllers(ApplicationModel application)
        {
            foreach (var controller in application.Controllers)
            {
                var controllerType = controller.ControllerType.AsType();
                var configuration = GetControllerSettingOrNull(controllerType);

                if (IsRemoteService(controllerType))
                {
                    controller.ControllerName = controller.ControllerName.RemovePostFix(ApplicationService.CommonPostfixes);
                    configuration?.ControllerModelConfigurer(controller);
                    //ConfigureArea(controller, configuration);
                    ConfigureRemoteService(controller, configuration);
                }
                else
                {
                    var remoteServiceAttr = ReflectionHelper.GetSingleAttributeOrDefault<RemoteServiceAttribute>(controllerType.GetTypeInfo());
                    if (remoteServiceAttr != null && remoteServiceAttr.IsEnabledFor(controllerType))
                    {
                        ConfigureRemoteService(controller, configuration);
                    }
                }
            }
        }

        protected virtual void ConfigureRemoteService(ControllerModel controller, [CanBeNull] AbpControllerAssemblySetting configuration)
        {
            ConfigureApiExplorer(controller);
            ConfigureSelector(controller, configuration);
            ConfigureParameters(controller);
        }

        protected virtual void ConfigureParameters(ControllerModel controller)
        {
            /* Default binding system of Asp.Net Core for a parameter
             * 1. Form values
             * 2. Route values.
             * 3. Query string.
             */

            foreach (var action in controller.Actions)
            {
                foreach (var prm in action.Parameters)
                {
                    if (prm.BindingInfo != null)
                    {
                        continue;
                    }

                    if (!TypeHelper.IsPrimitiveExtendedIncludingNullable(prm.ParameterInfo.ParameterType))
                    {
                        if (CanUseFormBodyBinding(action, prm))
                        {
                            prm.BindingInfo = BindingInfo.GetBindingInfo(new[] { new FromBodyAttribute() });
                        }
                    }
                }
            }
        }

        protected virtual bool CanUseFormBodyBinding(ActionModel action, ParameterModel parameter)
        {
            if (_options.AppServiceControllers.FormBodyBindingIgnoredTypes.Any(t => t.IsAssignableFrom(parameter.ParameterInfo.ParameterType)))
            {
                return false;
            }

            foreach (var selector in action.Selectors)
            {
                if (selector.ActionConstraints == null)
                {
                    continue;
                }

                foreach (var actionConstraint in selector.ActionConstraints)
                {
                    var httpMethodActionConstraint = actionConstraint as HttpMethodActionConstraint;
                    if (httpMethodActionConstraint == null)
                    {
                        continue;
                    }

                    if (httpMethodActionConstraint.HttpMethods.All(hm => hm.IsIn("GET", "DELETE", "TRACE", "HEAD")))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        protected virtual void ConfigureApiExplorer(ControllerModel controller)
        {
            if (controller.ApiExplorer.GroupName.IsNullOrEmpty())
            {
                controller.ApiExplorer.GroupName = controller.ControllerName;
            }

            if (controller.ApiExplorer.IsVisible == null)
            {
                var controllerType = controller.ControllerType.AsType();
                var remoteServiceAtt = ReflectionHelper.GetSingleAttributeOrDefault<RemoteServiceAttribute>(controllerType.GetTypeInfo());
                if (remoteServiceAtt != null)
                {
                    controller.ApiExplorer.IsVisible =
                        remoteServiceAtt.IsEnabledFor(controllerType) &&
                        remoteServiceAtt.IsMetadataEnabledFor(controllerType);
                }
                else
                {
                    controller.ApiExplorer.IsVisible = true;
                }
            }

            foreach (var action in controller.Actions)
            {
                ConfigureApiExplorer(action);
            }
        }

        protected virtual void ConfigureApiExplorer(ActionModel action)
        {
            if (action.ApiExplorer.IsVisible == null)
            {
                var remoteServiceAtt = ReflectionHelper.GetSingleAttributeOrDefault<RemoteServiceAttribute>(action.ActionMethod);
                if (remoteServiceAtt != null)
                {
                    action.ApiExplorer.IsVisible =
                        remoteServiceAtt.IsEnabledFor(action.ActionMethod) &&
                        remoteServiceAtt.IsMetadataEnabledFor(action.ActionMethod);
                }
            }
        }

        protected virtual void ConfigureSelector(ControllerModel controller, [CanBeNull] AbpControllerAssemblySetting configuration)
        {
            RemoveEmptySelectors(controller.Selectors);

            if (controller.Selectors.Any(selector => selector.AttributeRouteModel != null))
            {
                return;
            }

            var rootPath = GetRootPathOrDefault(controller.ControllerType.AsType());

            foreach (var action in controller.Actions)
            {
                ConfigureSelector(rootPath, controller.ControllerName, action, configuration);
            }
        }

        protected virtual void ConfigureSelector(string rootPath, string controllerName, ActionModel action, [CanBeNull] AbpControllerAssemblySetting configuration)
        {
            RemoveEmptySelectors(action.Selectors);

            if (!action.Selectors.Any())
            {
                AddAbpServiceSelector(rootPath, controllerName, action, configuration);
            }
            else
            {
                NormalizeSelectorRoutes(rootPath, controllerName, action);
            }
        }

        protected virtual void AddAbpServiceSelector(string rootPath, string controllerName, ActionModel action, [CanBeNull] AbpControllerAssemblySetting configuration)
        {
            var httpMethod = SelectHttpMethod(action, configuration);

            var abpServiceSelectorModel = new SelectorModel
            {
                AttributeRouteModel = CreateAbpServiceAttributeRouteModel(rootPath, controllerName, action, httpMethod),
                ActionConstraints = { new HttpMethodActionConstraint(new[] { httpMethod }) }
            };

            action.Selectors.Add(abpServiceSelectorModel);
        }

        protected virtual string SelectHttpMethod(ActionModel action, AbpControllerAssemblySetting configuration)
        {
            return HttpMethodHelper.GetConventionalVerbForMethodName(action.ActionName);
        }

        protected virtual void NormalizeSelectorRoutes(string rootPath, string controllerName, ActionModel action)
        {
            foreach (var selector in action.Selectors)
            {
                var httpMethod = selector.ActionConstraints.OfType<HttpMethodActionConstraint>().FirstOrDefault()?.HttpMethods?.FirstOrDefault();
                if (selector.AttributeRouteModel == null)
                {
                    selector.AttributeRouteModel = CreateAbpServiceAttributeRouteModel(rootPath, controllerName, action, httpMethod);
                }
            }
        }

        protected virtual string GetRootPathOrDefault(Type controllerType)
        {
            return GetControllerSettingOrNull(controllerType)?.RootPath ??
                   ModuleApiDescriptionModel.DefaultRootPath;
        }

        [CanBeNull]
        protected virtual AbpControllerAssemblySetting GetControllerSettingOrNull(Type controllerType)
        {
            return _options.AppServiceControllers.ControllerAssemblySettings.GetSettingOrNull(controllerType);
        }

        protected virtual AttributeRouteModel CreateAbpServiceAttributeRouteModel(string rootPath, string controllerName, ActionModel action, string httpMethod)
        {
            var url = CalculateUrl(rootPath, controllerName, action, httpMethod);

            return new AttributeRouteModel(new RouteAttribute(url));
        }

        protected virtual string CalculateUrl(string rootPath, string controllerName, ActionModel action, string httpMethod)
        {
            var url = $"api/{rootPath}/{controllerName.ToCamelCase()}";

            //Add {id} path if needed
            if (action.Parameters.Any(p => p.ParameterName == "id"))
            {
                url += "/{id}";
            }
            
            //Add action name if needed
            var actionNameInUrl = NormalizeUrlActionName(rootPath, controllerName, action, httpMethod);
            if (!actionNameInUrl.IsNullOrEmpty())
            {
                url += $"/{actionNameInUrl}";

                //Add secondary Id
                var secondaryIds = action.Parameters.Where(p => p.ParameterName.EndsWith("Id", StringComparison.Ordinal)).ToList();
                if (secondaryIds.Count == 1)
                {
                    url += $"/{{{secondaryIds[0].ParameterName}}}";
                }
            }

            return url;
        }

        protected virtual string NormalizeUrlActionName(string rootPath, string controllerName, ActionModel action, string httpMethod)
        {
            var context = new UrlActionNameNormalizerContext(rootPath, controllerName, action, httpMethod, action.ActionName);

            foreach (var normalizer in _options.AppServiceControllers.UrlActionNameNormalizers)
            {
                normalizer.Normalize(context);
            }

            return context.ActionNameInUrl;
        }

        protected virtual void RemoveEmptySelectors(IList<SelectorModel> selectors)
        {
            selectors
                .Where(IsEmptySelector)
                .ToList()
                .ForEach(s => selectors.Remove(s));
        }

        protected virtual bool IsEmptySelector(SelectorModel selector)
        {
            return selector.AttributeRouteModel == null && selector.ActionConstraints.IsNullOrEmpty();
        }

        protected virtual bool IsRemoteService(Type controllerType)
        {
            return typeof(IRemoteService).GetTypeInfo().IsAssignableFrom(controllerType);
        }
    }
}