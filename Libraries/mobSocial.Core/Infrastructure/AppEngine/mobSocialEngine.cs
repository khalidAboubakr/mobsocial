﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DryIoc;
using DryIoc.Mvc;
using DryIoc.Web;
using mobSocial.Core.Exception;
using mobSocial.Core.Infrastructure.DependencyManagement;
using mobSocial.Core.Infrastructure.Media;
using mobSocial.Core.Infrastructure.Utils;
using mobSocial.Core.Plugins;
using mobSocial.Core.Startup;
using mobSocial.Core.Tasks;
using DryIoc.SignalR;

namespace mobSocial.Core.Infrastructure.AppEngine
{
    public class mobSocialEngine : IAppEngine
    {
        public IContainer IocContainer { get; private set; }

        public IContainer MvcContainer { get; private set; }

        public static IList<PictureSize> PictureSizes { get; private set; }

        public T Resolve<T>(bool returnDefaultIfNotResolved = false) where T : class
        {
            T tInstance;
            try
            {
                tInstance = IocContainer.Resolve<T>(IfUnresolved.Throw);
            }
            catch
            {
                tInstance = MvcContainer.Resolve<T>(returnDefaultIfNotResolved ? IfUnresolved.ReturnDefault : IfUnresolved.Throw);
            }
            return tInstance;
        }

        public T RegisterAndResolve<T>(object instance = null, bool instantiateIfNull = true, IReuse reuse = null) where T : class
        {
            if (instance == null)
                if (instantiateIfNull)
                    instance = Activator.CreateInstance<T>();
                else
                {
                    throw new mobSocialException("Can't register a null instance");
                }
            var typedInstance = Resolve<T>(true);
            if (typedInstance == null)
            {
                if(IocContainer.GetCurrentScope() != null)
                    IocContainer.RegisterInstance<T>(instance as T, reuse, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                if(MvcContainer.GetCurrentScope() != null)
                    MvcContainer.RegisterInstance<T>(instance as T, reuse, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                typedInstance = instance as T;
            }
            return typedInstance;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Start(bool testMode = false)
        {
            //setup ioc container
            SetupContainer();

            //setup dependency resolvers
            SetupDependencies(testMode);

            if (!testMode)
            {
                //run startup tasks
                RunStartupTasks();

                //start task manager to run scheduled tasks
                StartTaskManager();
            }

            SetupMvcContainer();
        }

        public void SetupContainer()
        {
            var assemblies = AssemblyLoader.GetAppDomainAssemblies().Where(x => x.FullName.StartsWith("mobSocial")).ToArray();
            IocContainer = new Container(rules => rules.WithoutThrowIfDependencyHasShorterReuseLifespan(), new AsyncExecutionFlowScopeContext()).WithSignalR(assemblies.ToArray());
        }

        public void SetupMvcContainer()
        {
            MvcContainer = IocContainer.WithMvc(scopeContext: new HttpContextScopeContext());
        }
        private void SetupDependencies(bool testMode = false)
        {
            //first the self
            IocContainer.Register<IAppEngine, mobSocialEngine>(Reuse.Singleton);

            //now the other dependencies by other plugins or system
            var dependencies = TypeFinder.ClassesOfType<IDependencyRegistrar>();
            //create instances for them
            var dependencyInstances = dependencies.Select(dependency => (IDependencyRegistrar)Activator.CreateInstance(dependency)).ToList();
            //reorder according to priority
            dependencyInstances = dependencyInstances.OrderBy(x => x.Priority).ToList();

            foreach (var di in dependencyInstances)
                //register individual instances in that order
                di.RegisterDependencies(IocContainer);
        }

        private void RunStartupTasks()
        {
            var startupTasks = TypeFinder.ClassesOfType<IStartupTask>();
            var tasks =
                startupTasks.Select(startupTask => (IStartupTask)Activator.CreateInstance(startupTask)).ToList();

            //reorder according to prioiryt
            tasks = tasks.OrderBy(x => x.Priority).ToList();

            foreach (var task in tasks)
                task.Run();
            ;

        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void StartTaskManager()
        {
            var taskManager = ActiveEngine.Resolve<ITaskManager>();
            var tasks = TypeFinder.ClassesOfType<ITask>();
            taskManager?.Start(tasks.ToArray());
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void SetupPictureSizes(bool testMode = false)
        {
            if (PictureSizes != null && PictureSizes.Count > 0)
                return; //already registered
            PictureSizes = PictureSizes ?? new List<PictureSize>();
            if (testMode)
                return;
            var allPictureSizeRegistrars = TypeFinder.ClassesOfType<IPictureSizeRegistrar>();
            var allPictureSizeInstances =
                allPictureSizeRegistrars.Select(x => (IPictureSizeRegistrar)Activator.CreateInstance(x));

            foreach (var sizeInstance in allPictureSizeInstances)
                sizeInstance.RegisterPictureSize(PictureSizes);
        }

        private void InstallSystemPlugins()
        {
            var allPlugins = TypeFinder.ClassesOfType<IPlugin>();

            var systemPlugins =
                allPlugins.Where(x => (x as IPlugin).IsSystemPlugin)
                    .Select(plugin => (IPlugin)Activator.CreateInstance(plugin))
                    .ToList();

            //run the install method
            foreach (var plugin in systemPlugins)
            {
                if (!PluginEngine.IsInstalled(plugin.PluginInfo))
                    plugin.Install();
            }

        }

        public static mobSocialEngine ActiveEngine
        {
            get { return Singleton<mobSocialEngine>.Instance; }
        }
    }
}