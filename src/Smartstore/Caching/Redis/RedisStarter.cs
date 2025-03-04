﻿using Autofac;
using Microsoft.Extensions.Configuration;
using Smartstore.Caching;
using Smartstore.Engine;
using Smartstore.Engine.Builders;
using Smartstore.Events;
using Smartstore.Redis.Caching;
using Smartstore.Redis.Configuration;
using Smartstore.Redis.Threading;
using Smartstore.Threading;

namespace Smartstore.Redis
{
    internal sealed class RedisStarter : StarterBase
    {
        public override int Order => (int)StarterOrdering.Default;

        public override bool Matches(IApplicationContext appContext)
            => appContext.IsInstalled;

        public override void ConfigureContainer(ContainerBuilder builder, IApplicationContext appContext)
        {
            // Create and register configuration
            var config = appContext.Configuration;
            var redisConfiguration = new RedisConfiguration();
            config.Bind("Smartstore:Redis", redisConfiguration);
            builder.RegisterInstance(redisConfiguration);

            var hasDefaultConString = redisConfiguration.ConnectionStrings.Default.HasValue();
            var hasCacheConString = redisConfiguration.ConnectionStrings.Cache.HasValue() || hasDefaultConString;
            var hasMessageBusConString = redisConfiguration.ConnectionStrings.Bus.HasValue() || hasDefaultConString;

            builder.RegisterType<RedisConnectionFactory>()
                .As<IRedisConnectionFactory>()
                .SingleInstance();

            if (hasMessageBusConString)
            {
                builder.Register<RedisMessageBus>(ResolveDefaultMessageBus)
                    .As<IMessageBus>()
                    .AsSelf()
                    .SingleInstance();
            }

            if (hasCacheConString)
            {
                builder.RegisterType<RedisAsyncState>()
                    .As<IAsyncState>()
                    .AsSelf()
                    .SingleInstance();
            }

            if (hasCacheConString)
            {
                builder.RegisterType<RedisCacheStore>()
                    .As<ICacheStore>()
                    .As<IDistributedCacheStore>()
                    .AsSelf()
                    .SingleInstance();
            }
        }

        private static RedisMessageBus ResolveDefaultMessageBus(IComponentContext ctx)
        {
            var connectionFactory = ctx.Resolve<IRedisConnectionFactory>();
            var connectionStrings = ctx.Resolve<RedisConfiguration>().ConnectionStrings;
            var connectionString = connectionStrings.Bus ?? connectionStrings.Default;

            return connectionFactory.GetMessageBus(connectionString);
        }
    }
}