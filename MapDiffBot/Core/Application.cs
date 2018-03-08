﻿using Hangfire;
using Hangfire.MySql;
using Hangfire.SqlServer;
using MapDiffBot.Configuration;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ZNetCS.AspNetCore.Logging.EntityFrameworkCore;

namespace MapDiffBot.Core
{
	/// <summary>
	/// Startup point for the web application
	/// </summary>
	public class Application
	{
		/// <summary>
		/// The user agent string to provide to various APIs
		/// </summary>
		public static readonly string UserAgent = String.Format(CultureInfo.InvariantCulture, "MapDiffBot-v{0}", Assembly.GetExecutingAssembly().GetName().Version);

		/// <summary>
		/// The <see cref="IConfiguration"/> for the <see cref="Application"/>
		/// </summary>
		readonly IConfiguration configuration;

		/// <summary>
		/// Construct an <see cref="Application"/>
		/// </summary>
		/// <param name="configuration">The value of <see cref="configuration"/></param>
		public Application(IConfiguration configuration)
		{
			this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}


		/// <summary>
		/// Configure dependency injected services
		/// </summary>
		/// <param name="services">The <see cref="IServiceCollection"/> to configure</param>
		public void ConfigureServices(IServiceCollection services)
		{
			if (services == null)
				throw new ArgumentNullException(nameof(services));


			services.Configure<IISOptions>((options) => options.ForwardClientCertificate = false);

			services.Configure<GitHubConfiguration>(configuration.GetSection(GitHubConfiguration.Section));
			var dbConfigSection = configuration.GetSection(DatabaseConfiguration.Section);
			services.Configure<DatabaseConfiguration>(dbConfigSection);

			services.AddHangfire((builder) =>
			{
				var dbConfig = dbConfigSection.Get<DatabaseConfiguration>();
				if (dbConfig.IsMySQL)
					builder.UseStorage(new MySqlStorage(dbConfig.ConnectionString, new MySqlStorageOptions { PrepareSchemaIfNecessary = true }));
				else
					builder.UseSqlServerStorage(dbConfig.ConnectionString, new SqlServerStorageOptions { PrepareSchemaIfNecessary = true });
			});

			services.Configure<MvcOptions>(options => options.Filters.Add(new RequireHttpsAttribute()));

			services.Configure<IISOptions>((options) => options.ForwardClientCertificate = false);
			services.AddMvc();
			services.AddOptions();

			services.AddDbContext<DatabaseContext>();
			services.AddScoped<IGitHubClientFactory, GitHubClientFactory>();
			services.AddScoped<IGitHubManager, GitHubManager>();
		}

		/// <summary>
		/// Configure the <see cref="Application"/>
		/// </summary>
		/// <param name="applicationBuilder">The <see cref="IApplicationBuilder"/> to configure</param>
		/// <param name="hostingEnvironment">The <see cref="IHostingEnvironment"/> of the <see cref="Application"/></param>
		/// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to configure</param>
		/// <param name="databaseContext">The <see cref="IDatabaseContext"/> to configure</param>
		/// <param name="applicationLifetime">The <see cref="IApplicationLifetime"/> to use <see cref="System.Threading.CancellationToken"/>s from</param>
		public void Configure(IApplicationBuilder applicationBuilder, IHostingEnvironment hostingEnvironment, ILoggerFactory loggerFactory, IApplicationLifetime applicationLifetime, IDatabaseContext databaseContext)
		{
			if (applicationBuilder == null)
				throw new ArgumentNullException(nameof(applicationBuilder));
			if (hostingEnvironment == null)
				throw new ArgumentNullException(nameof(hostingEnvironment));
			if (loggerFactory == null)
				throw new ArgumentNullException(nameof(loggerFactory));
			if (applicationLifetime == null)
				throw new ArgumentNullException(nameof(applicationLifetime));
			if (databaseContext == null)
				throw new ArgumentNullException(nameof(databaseContext));

			//prevent telemetry from polluting the debug log
			TelemetryConfiguration.Active.DisableTelemetry = true;

			databaseContext.Initialize(applicationLifetime.ApplicationStopping).GetAwaiter().GetResult();

			loggerFactory.AddEntityFramework<DatabaseContext>(applicationBuilder.ApplicationServices);

			if (hostingEnvironment.IsDevelopment())
				applicationBuilder.UseDeveloperExceptionPage();

			var defaultCulture = new CultureInfo("en");
			var supportedCultures = new List<CultureInfo>
			{
				defaultCulture
			};

			CultureInfo.CurrentCulture = defaultCulture;
			CultureInfo.CurrentUICulture = defaultCulture;

			applicationBuilder.UseRequestLocalization(new RequestLocalizationOptions
			{
				SupportedCultures = supportedCultures,
				SupportedUICultures = supportedCultures,
			});

			applicationBuilder.UseHangfireServer();

			applicationBuilder.UseStaticFiles();

			applicationBuilder.UseRewriter(new RewriteOptions().AddRedirectToHttpsPermanent());

			applicationBuilder.UseMvc();
        }
    }
}