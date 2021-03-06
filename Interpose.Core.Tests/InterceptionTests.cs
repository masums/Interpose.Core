﻿using Interpose.Core.Generators;
using Interpose.Core.Handlers;
using Interpose.Core.Interceptors;
using Interpose.Core.Proxies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Transactions;
using Xunit;

namespace Interpose.Core.Tests
{
    public class InterceptionTests
	{
        #region Handlers library
        [Fact]
        public void CanRaiseNotifyChangeEvents()
        {
            var instance = new Notifiable();
            var interceptor = new InterfaceInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(INotifiable), new NotifyPropertyChangeHandler()) as INotifiable;

            var propertyChangingFired = false;
            var propertyChangedFired = false;

            proxy.PropertyChanging += (sender, args) => propertyChangingFired = true;
            proxy.PropertyChanged += (sender, args) => propertyChangedFired = true;

            proxy.Name = "";

            Assert.True(propertyChangingFired);
            Assert.True(propertyChangedFired);
        }

        [Fact]
        public void CanValidate()
        {
            var instance = new Validatable();
            var interceptor = new InterfaceInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(IValidatable), new ValidationInterceptionHandler()) as IValidatable;
            Assert.Throws<ValidationException>(() => proxy.Try());
        }

        [Fact]
        public void CanCreateTransactions()
        {
            var instance = new Calculator();
            var interceptor = new InterfaceInterceptor();
            var handler = new MultiInterceptionHandler();
            var result = false;
            handler.Handlers.Add(new TransactionInterceptionHandler(TimeSpan.FromSeconds(10), System.Transactions.TransactionScopeAsyncFlowOption.Enabled, System.Transactions.TransactionScopeOption.Required, System.Transactions.IsolationLevel.ReadCommitted));
            handler.Handlers.Add(new DelegateInterceptionHandler(ctx =>
            {
                result = Transaction.Current != null;
            }));
            var proxy = interceptor.Intercept(instance, typeof(ICalculator), handler) as ICalculator;
            proxy.Add(1, 2);
            Assert.True(result);
        }

        [Fact]
        public void CanCache()
        {
            var instance = new Calculator();
            var interceptor = new InterfaceInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(ICalculator), new CachingInterceptionHandler(new MemoryCache(Options.Create<MemoryCacheOptions>(new MemoryCacheOptions())), TimeSpan.FromSeconds(10))) as ICalculator;

            proxy.Add(1, 2);

            var timer = Stopwatch.StartNew();
            proxy.Add(1, 2);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void CanRetry()
        {
            var instance = new ErrorOperation();
            var interceptor = new InterfaceInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(IErrorOperation), new RetriesInterceptionHandler(3, TimeSpan.FromSeconds(5))) as IErrorOperation;
            Assert.Throws<InvalidOperationException>(() =>
                proxy.Throw()
            );
        }

        [Fact]
        public void CanMakeAsync()
        {
            var instance = new LongWait();
            var interceptor = new DispatchProxyInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(ILongWait), new AsyncInterceptionHandler()) as ILongWait;
            var timer = Stopwatch.StartNew();
            proxy.DoLongOperation();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void CanLog()
        {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddConsole();
            loggerFactory.AddDebug();

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(loggerFactory);
            services.AddInterfaceInterceptor();
            services.AddLogging();
            services.AddLoggingHandler(LogLevel.Critical);

            var serviceProvider = services.BuildServiceProvider();

            var instance = new MyType();
            var interceptor = serviceProvider.GetRequiredService<IInstanceInterceptor>();

            var proxy = interceptor.Intercept(instance, typeof(IMyType), serviceProvider.GetRequiredService<LoggingInterceptionHandler>()) as IMyType;
            proxy.MyMethod();
        }

        [Fact]
        public void CanControlAccess()
        {
            var instance = new ProtectedClass();
            var interceptor = new InterfaceInterceptor();
            var proxy = interceptor.Intercept(instance, typeof(IProtectedClass), new AccessControlInterceptionHandler("")) as IProtectedClass;
            Assert.Throws<InvalidOperationException>(() =>
                proxy.TryAccess()
            );
        }
        #endregion Handlers library

        [Fact]
        public void CanUseDependencyInjection()
        {
            var services = new ServiceCollection();
            services.AddInterfaceInterceptor();
            services.AddVirtualMethodInterceptor();
            services.AddDynamicInterceptor();

            var serviceProvider = services.BuildServiceProvider();

            var typeInterceptors = serviceProvider.GetServices<ITypeInterceptor>();
            var instanceInterceptors = serviceProvider.GetServices<IInstanceInterceptor>();

            Assert.NotEmpty(typeInterceptors);
            Assert.NotEmpty(instanceInterceptors);
        }

        private object InstanceInterception(IInstanceInterceptor interceptor, object instance, IInterceptionHandler handler)
        {
            var canIntercept = interceptor.CanIntercept(instance);

            Assert.True(canIntercept);

            var proxy = interceptor.Intercept(instance, handler);

            var interceptionProxy = proxy as IInterceptionProxy;

            Assert.NotNull(interceptionProxy);

            var otherInterceptor = interceptionProxy.Interceptor;

            Assert.Equal(otherInterceptor, interceptor);

            var target = interceptionProxy.Target;

            Assert.Equal(target, instance);

            return proxy;
        }

        [Fact]
        public void CanDoDynamicInterception()
        {
            var instance = new MyType();
            var interceptor = new DynamicInterceptor();
            var handler = new ModifyResultHandler();

            //dynamic proxy = this.InstanceInterception(interceptor, instance, handler);
            dynamic proxy = instance.InterceptDynamic(handler);

            var result = proxy.MyMethod();

            Assert.Equal(20, result);
        }

        [Fact]
        public void CanCacheInterfaceGeneration()
        {
            var interceptor = new InterfaceInterceptor();
            var generator = new RoslynInterceptedTypeGenerator().AsCached();

            var proxyType1 = generator.Generate(interceptor, typeof(MyType), typeof(ModifyResultHandler));
            var proxyType2 = generator.Generate(interceptor, typeof(MyType), typeof(ModifyResultHandler));

            Assert.Equal(proxyType1, proxyType2);
        }

        [Fact]
        public void CanCallBaseImplementation()
        {
            var instance = new MyType();
            var interceptor = new DispatchProxyInterceptor();
            var handler = new DelegateInterceptionHandler(arg => arg.Proceed());

            var proxy = this.InstanceInterception(interceptor, instance, handler) as IMyType;

            var result = proxy.MyMethod();

            Assert.Equal(0, result);
        }

        [Fact]
        public void CanDoDispatchProxyInterception()
        {
            var instance = new MyType();
            var interceptor = new DispatchProxyInterceptor();
            var handler = new ModifyResultHandler();

            var proxy = this.InstanceInterception(interceptor, instance, handler) as IMyType;

            var result = proxy.MyMethod();

            Assert.Equal(20, result);
        }

        [Fact]
        public void CanDoInterfaceInterception()
        {
            var instance = new MyType();
            var interceptor = new InterfaceInterceptor();
            var handler = new ModifyResultHandler();

            var proxy = this.InstanceInterception(interceptor, instance, handler) as IMyType;

            var result = proxy.MyMethod();

            Assert.Equal(20, result);
        }

		[Fact]
		public void CanDoVirtualInterception()
		{
			//Virtual method interceptor
			var type = typeof(MyType);
			var handlerType = typeof(ModifyResultHandler);
			var interceptor = new VirtualMethodInterceptor();
			var canIntercept = interceptor.CanIntercept(type);
			var myProxyType = interceptor.Intercept(type, handlerType);
			var myProxy = Activator.CreateInstance(myProxyType) as IMyType;
			var result = myProxy.MyMethod();
			Assert.Equal(20, result);
		}

		[Fact]
		public void CanDoDynamicInterceptionWithAttributes()
		{
			//interception through attributes
			var instance = new MyType3();
			dynamic myProxy = DynamicInterceptor.Instance.InterceptWithAttributes(instance);
            var result = myProxy.MyMethod();
            Assert.Equal(20, result);
        }

		[Fact]
		public void CanDoDynamicInterceptionWithRegistry()
		{
			//interception through a registry
			var instance = new MyType3();
			var interceptor = new DynamicInterceptor();
			var registry = new RegistryInterceptionHandler()
			    .Register<MyType3>(x => x.MyProperty, new ModifyResultHandler())
			    .Register<MyType3>(x => x.MyMethod(), new ModifyResultHandler());
			dynamic myProxy = interceptor.Intercept(instance, typeof(IMyType), registry);
            var result = myProxy.MyMethod();
            Assert.Equal(20, result);
        }

        [Fact]
        public void CanEnforceTimeout()
        {
            var instance = new LongWait();
            var interceptor = new InterfaceInterceptor();
            var handler = new TimeoutInterceptionHandler(3);

            var proxy = this.InstanceInterception(interceptor, instance, handler) as ILongWait;

            Assert.Throws<TimeoutException>(() => proxy.DoLongOperation());
        }

        [Fact]
        public void CanDoCallback()
        {
            var called = false;
            var instance = new LongWait();
            var interceptor = new InterfaceInterceptor();
            var handler = new CallbackInterceptionHandler(() => called = true);

            var proxy = this.InstanceInterception(interceptor, instance, handler) as ILongWait;

            proxy.DoLongOperation();

            Assert.True(called);
        }
    }
}
