﻿using Hangfire;
using MapDiffBot.Configuration;
using MapDiffBot.Controllers;
using MapDiffBot.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Z.EntityFramework.Plus;

namespace MapDiffBot.Core
{
	/// <inheritdoc />
#pragma warning disable CA1812
	sealed class PayloadProcessor : IPayloadProcessor
#pragma warning restore CA1812
	{
		/// <summary>
		/// The intermediate directory for operations
		/// </summary>
		public const string WorkingDirectory = "MapDiffs";

		/// <summary>
		/// The <see cref="GitHubConfiguration"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly GitHubConfiguration gitHubConfiguration;
		/// <summary>
		/// The <see cref="IGeneratorFactory"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly IGeneratorFactory generatorFactory;
		/// <summary>
		/// The <see cref="IGitHubManager"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly IServiceProvider serviceProvider;
		/// <summary>
		/// The <see cref="ILocalRepositoryManager"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly ILocalRepositoryManager repositoryManager;
		/// <summary>
		/// The <see cref="IIOManager"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly IIOManager ioManager;
		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly ILogger<PayloadProcessor> logger;
		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly IStringLocalizer<PayloadProcessor> stringLocalizer;
		/// <summary>
		/// The <see cref="IBackgroundJobClient"/> for the <see cref="PayloadProcessor"/>
		/// </summary>
		readonly IBackgroundJobClient backgroundJobClient;

		/// <summary>
		/// <see cref="Dictionary{TKey, TValue}"/> of operation name to their <see cref="CancellationToken"/>
		/// </summary>
		readonly Dictionary<string, CancellationTokenSource> mapDiffOperations;
		
		public PayloadProcessor(IOptions<GitHubConfiguration> gitHubConfigurationOptions, IGeneratorFactory generatorFactory, IServiceProvider serviceProvider, ILocalRepositoryManager repositoryManager, IIOManager ioManager, ILogger<PayloadProcessor> logger, IStringLocalizer<PayloadProcessor> stringLocalizer, IBackgroundJobClient backgroundJobClient)
		{
			gitHubConfiguration = gitHubConfigurationOptions?.Value ?? throw new ArgumentNullException(nameof(gitHubConfigurationOptions));
			this.ioManager = new ResolvingIOManager(ioManager ?? throw new ArgumentNullException(nameof(ioManager)), WorkingDirectory);
			this.generatorFactory = generatorFactory ?? throw new ArgumentNullException(nameof(generatorFactory));
			this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
			this.repositoryManager = repositoryManager ?? throw new ArgumentNullException(nameof(repositoryManager));
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.stringLocalizer = stringLocalizer ?? throw new ArgumentNullException(nameof(stringLocalizer));
			this.backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));

			mapDiffOperations = new Dictionary<string, CancellationTokenSource>();
		}

		/// <summary>
		/// Generates a map diff comment for the specified <see cref="PullRequest"/>
		/// </summary>
		/// <param name="jobSubmission">The <see cref="JobSubmission"/> for the operation</param>
		/// <param name="jobCancellationToken">The <see cref="IJobCancellationToken"/> for the operation</param>
		/// <returns>A <see cref="Task"/> representing the running operation</returns>
		[AutomaticRetry(Attempts = 0)]
		[DisplayName("{0}")]
		public async Task ScanPullRequest(JobSubmission jobSubmission, IJobCancellationToken jobCancellationToken)
		{
			using (serviceProvider.CreateScope())
			{
				var gitHubManager = serviceProvider.GetRequiredService<IGitHubManager>();
				var pullRequest = await gitHubManager.GetPullRequest(jobSubmission.RepositoryId, jobSubmission.PullRequestNumber, jobCancellationToken.ShutdownToken).ConfigureAwait(false);
				
				var changedMapsTask = gitHubManager.GetPullRequestChangedFiles(pullRequest, jobCancellationToken.ShutdownToken);
				var requestIdentifier = String.Concat(pullRequest.Base.Repository.Owner.Login, pullRequest.Base.Repository.Name, pullRequest.Number);

				//Generate our own cancellation token for rolling builds of the same PR
				using (var cts = new CancellationTokenSource())
				using (jobCancellationToken.ShutdownToken.Register(() => cts.Cancel()))
				{
					var cancellationToken = cts.Token;

					lock (mapDiffOperations)
					{
						if (mapDiffOperations.TryGetValue(requestIdentifier, out CancellationTokenSource oldOperation))
						{
							oldOperation.Cancel();
							mapDiffOperations[requestIdentifier] = cts;
						}
						else
							mapDiffOperations.Add(requestIdentifier, cts);
					}

					var errors = new List<Exception>();
					try
					{
						for (var I = 0; !pullRequest.Mergeable.HasValue && I < 5; cancellationToken.ThrowIfCancellationRequested(), cancellationToken.ThrowIfCancellationRequested(), ++I)
						{
							await Task.Delay(1000 * I, cancellationToken).ConfigureAwait(false);
							pullRequest = await gitHubManager.GetPullRequest(pullRequest.Base.Repository.Id, pullRequest.Number, cancellationToken).ConfigureAwait(false); ;
						}

						if (!pullRequest.Mergeable.HasValue || !pullRequest.Mergeable.Value)
							return;

						var allChangedMaps = await changedMapsTask.ConfigureAwait(false);
						var changedDmms = allChangedMaps.Where(x => x.FileName.EndsWith(".dmm", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.FileName).ToList();
						if (changedDmms.Count == 0)
							return;

						await GenerateDiffs(pullRequest, changedDmms, jobSubmission.BaseUrl, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						//if this is the only exception, throw it directly, otherwise pile it in the exception collection
						if (errors.Count == 0)
							throw;
						errors.Add(e);
						cts.Cancel();
					}
					finally
					{
						lock (mapDiffOperations)
							if (mapDiffOperations.TryGetValue(requestIdentifier, out CancellationTokenSource maybeOurOperation) && maybeOurOperation == cts)
								mapDiffOperations.Remove(requestIdentifier);
						//throw all generator errors at once, because we can allow things to continue if some fail
						if (errors.Count > 0)
						{
							var e = new AggregateException(String.Format(CultureInfo.CurrentCulture, "Repo: {0}/{1}, PR: {2} (#{3}) Base: {4} ({5}), HEAD: {6}", pullRequest.Base.Repository.Owner.Login, pullRequest.Base.Repository.Name, pullRequest.Title, pullRequest.Number, pullRequest.Base.Sha, pullRequest.Base.Label, pullRequest.Head.Sha), errors);
							logger.LogError(e, "Generation errors occurred!");
						}
					}
				}
			}
		}

		/// <summary>
		/// Generate map diffs for a given <paramref name="pullRequest"/>
		/// </summary>
		/// <param name="pullRequest">The <see cref="PullRequest"/></param>
		/// <param name="changedDmms">Paths to changed .dmm files</param>
		/// <param name="baseUrl">The base URL of the request</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation</param>
		/// <returns>A <see cref="Task"/> representing the running operation</returns>
		async Task GenerateDiffs(PullRequest pullRequest, IReadOnlyList<string> changedDmms, string baseUrl, CancellationToken cancellationToken)
		{
			var gitHubManager = serviceProvider.GetRequiredService<IGitHubManager>();
			Task CreateComment(string commentKey) => gitHubManager.CreateSingletonComment(pullRequest, stringLocalizer[commentKey], cancellationToken);

			Task generatingCommentTask;
			List<Task<RenderResult>> beforeRenderings, afterRenderings;
			IIOManager currentIOManager = new ResolvingIOManager(ioManager, ioManager.ConcatPath(pullRequest.Base.Repository.Owner.Login, pullRequest.Base.Repository.Name, pullRequest.Number.ToString(CultureInfo.InvariantCulture)));
			string repoPath;
			using (var repo = await repositoryManager.GetRepository(pullRequest.Base.Repository, () => CreateComment("Cloning repository..."), () => CreateComment("Waiting for another operation on this repository to complete..."), cancellationToken).ConfigureAwait(false))
			{
				generatingCommentTask = gitHubManager.CreateSingletonComment(pullRequest, stringLocalizer["Generating diffs..."], cancellationToken);
				//prep the outputDirectory
				async Task DirectoryPrep()
				{
					await currentIOManager.DeleteDirectory(".", cancellationToken).ConfigureAwait(false);
					await currentIOManager.CreateDirectory(".", cancellationToken).ConfigureAwait(false);
				};
				
				var dirPrepTask = DirectoryPrep();
				//get the dme to use
				var dmeToUseTask = serviceProvider.GetRequiredService<IDatabaseContext>().InstallationRepositories.Where(x => x.Id == pullRequest.Base.Repository.Id).Select(x => x.TargetDme).ToAsyncEnumerable().FirstOrDefault(cancellationToken);

				var oldMapPaths = new List<string>()
				{
					Capacity = changedDmms.Count
				};
				try
				{                  
					//fetch base commit if necessary and check it out, fetch pull request
					if (!await repo.ContainsCommit(pullRequest.Base.Sha, cancellationToken).ConfigureAwait(false))
						await repo.Fetch(cancellationToken).ConfigureAwait(false);
					await repo.Checkout(pullRequest.Base.Sha, cancellationToken).ConfigureAwait(false);

					//but since we don't need this right await don't await it yet
					var pullRequestFetchTask = repo.FetchPullRequest(pullRequest.Number, cancellationToken);
					try
					{
						//first copy all modified maps to the same location with the .old_map_diff_bot extension
						async Task<string> CacheMap(string mapPath)
						{
							var originalPath = currentIOManager.ConcatPath(repoPath, mapPath);
							if (await currentIOManager.FileExists(originalPath, cancellationToken).ConfigureAwait(false))
							{
								var oldMapPath = String.Format(CultureInfo.InvariantCulture, "{0}.old_map_diff_bot", originalPath);
								await currentIOManager.CopyFile(originalPath, oldMapPath, cancellationToken).ConfigureAwait(false);
								return oldMapPath;
							}
							return null;
						};

						repoPath = repo.Path;

						var tasks = changedDmms.Select(x => CacheMap(x)).ToList();
						await Task.WhenAll(tasks).ConfigureAwait(false);
						oldMapPaths.AddRange(tasks.Select(x => x.Result));
					}
					finally
					{
						await pullRequestFetchTask.ConfigureAwait(false);
					}
					//generate the merge commit ourselves since we can't get it from GitHub because itll return an outdated one
					await repo.Merge(pullRequest.Head.Sha, cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					await dmeToUseTask.ConfigureAwait(false);
				}
				
				//create empty array of map regions
				var mapRegions = Enumerable.Repeat<MapRegion>(null, changedDmms.Count).ToList();
				var dmeToUse = dmeToUseTask.Result;

				using (var generator = generatorFactory.CreateGenerator(dmeToUse, new ResolvingIOManager(ioManager, repoPath)))
				{
					var outputDirectory = currentIOManager.ResolvePath(".");
					//Generate MapRegions for modified maps and render all new maps
					async Task<RenderResult> DiffAndRenderNewMap(int I)
					{
						await dirPrepTask.ConfigureAwait(false);
						var originalPath = currentIOManager.ConcatPath(repoPath, changedDmms[I]);
						if (!await currentIOManager.FileExists(originalPath, cancellationToken).ConfigureAwait(false))
							return new RenderResult { InputPath = changedDmms[I], ToolOutput = stringLocalizer["Map missing!"] };
						if (oldMapPaths[I] != null)
						{
							var result = await generator.GetDifferences(oldMapPaths[I], originalPath, cancellationToken).ConfigureAwait(false);
							var region = result.MapRegion;
							if (region != null)
							{
								var xdiam = region.MaxX - region.MinX;
								var ydiam = region.MaxY - region.MinY;
								const int minDiffDimensions = 5 - 1;
								if (xdiam < minDiffDimensions || ydiam < minDiffDimensions)
								{
									//need to expand
									var fullResult = await generator.GetMapSize(originalPath, cancellationToken).ConfigureAwait(false);
									var fullRegion = fullResult.MapRegion;
									if (fullRegion == null)
									{
										//give up
										region = null;
									}
									else
									{
										bool increaseMax = true;
										if (xdiam < minDiffDimensions && ((fullRegion.MaxX - fullRegion.MinX) >= minDiffDimensions))
											while ((region.MaxX - region.MinX) < minDiffDimensions)
											{
												if (increaseMax)
													region.MaxX = (short)Math.Min(region.MaxX + 1, fullRegion.MaxX);
												else
													region.MinX = (short)Math.Max(region.MinX - 1, 1);
												increaseMax = !increaseMax;
											}
										if (ydiam < minDiffDimensions && ((fullRegion.MaxY - fullRegion.MinY) >= minDiffDimensions))
											while ((region.MaxY - region.MinY) < minDiffDimensions)
											{
												if (increaseMax)
													region.MaxY = (short)Math.Min(region.MaxY + 1, fullRegion.MaxY);
												else
													region.MinY = (short)Math.Max(region.MinY - 1, 1);
												increaseMax = !increaseMax;
											}
									}
								}
								mapRegions[I] = region;
							}
						}
						return await generator.RenderMap(originalPath, mapRegions[I], outputDirectory, "after", cancellationToken).ConfigureAwait(false);
					};

					//finish up before we go back to the base branch
					beforeRenderings = Enumerable.Range(0, changedDmms.Count).Select(I => DiffAndRenderNewMap(I)).ToList();
					try
					{
						await Task.WhenAll(beforeRenderings).ConfigureAwait(false);
					}
					catch (Exception)
					{
						//at this point everything is done but some have failed
						//we'll handle it later
					}
					await repo.Checkout(pullRequest.Base.Sha, cancellationToken).ConfigureAwait(false);

					Task<RenderResult> RenderOldMap(int i)
					{
						var oldPath = oldMapPaths[i];
						if (oldMapPaths != null)
							return generator.RenderMap(oldPath, mapRegions[i], outputDirectory, "before", cancellationToken);
						return Task.FromResult(new RenderResult { InputPath = changedDmms[i], ToolOutput = stringLocalizer["Map missing!"] });
					}

					//finish up rendering
					afterRenderings = Enumerable.Range(0, changedDmms.Count).Select(I => RenderOldMap(I)).ToList();
					try
					{
						await Task.WhenAll(afterRenderings).ConfigureAwait(false);
					}
					catch (Exception)
					{
						//see above
					}
				}
				//done with the repo at this point
			}

			//collect results and errors
			async Task<MapDiff> GetResult(int i)
			{
				var beforeTask = beforeRenderings[i];
				var afterTask = afterRenderings[i];

				var result = new MapDiff
				{
					RepositoryId = pullRequest.Base.Repository.Id,
					PullRequestNumber = pullRequest.Number,
					FileId = i,
				};

				RenderResult GetRenderingResult(Task<RenderResult> task)
				{
					if (task.Exception != null)
					{
						result.LogMessage = String.Format(CultureInfo.InvariantCulture, "{0}{1}{2}", result.LogMessage, Environment.NewLine, task.Exception);
						return null;
					}
					return task.Result;
				};

				var r1 = GetRenderingResult(beforeTask);
				var r2 = GetRenderingResult(afterTask);

				result.MapRegion = r1?.MapRegion ?? r2?.MapRegion;
				result.MapPath = r1?.InputPath ?? r2.InputPath;

				result.LogMessage = String.Format(CultureInfo.InvariantCulture, "Job {5}:Path: {6}{0}Before:{0}Command Line: {1}{0}Output:{0}{2}{0}After:{0}Command Line: {3}{0}Output:{4}", Environment.NewLine, r1?.CommandLine, r1?.OutputPath, r2?.CommandLine, r2?.OutputPath, i + 1, result.MapPath);

				result.MapPath = result.MapPath.Replace(repoPath, String.Empty, StringComparison.InvariantCultureIgnoreCase);
				result.MapPath = result.MapPath.Substring(1);

				async Task<Image> ReadMapImage(string path)
				{
					if (path != null && await currentIOManager.FileExists(path, cancellationToken).ConfigureAwait(false))
					{
						var bytes = await currentIOManager.ReadAllBytes(path, cancellationToken).ConfigureAwait(false);
						await currentIOManager.DeleteFile(path, cancellationToken).ConfigureAwait(false);
						return new Image { Data = bytes };
					}
					return null;
				}

				var readBeforeTask = ReadMapImage(r1?.OutputPath);
				result.AfterImage = await ReadMapImage(r2?.OutputPath).ConfigureAwait(false);
				result.BeforeImage = await readBeforeTask.ConfigureAwait(false);

				return result;
			}

			await generatingCommentTask.ConfigureAwait(false);

			var results = Enumerable.Range(0, changedDmms.Count).Select(x => GetResult(x)).ToList();
			await Task.WhenAll(results).ConfigureAwait(false);
			await HandleResults(pullRequest, results.Select(x => x.Result).ToList(), baseUrl, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Publish a <see cref="List{T}"/> of <paramref name="diffResults"/>s to <paramref name="baseUrl"/>
		/// </summary>
		/// <param name="pullRequest">The <see cref="PullRequest"/> the <paramref name="diffResults"/> are for</param>
		/// <param name="diffResults">The <see cref="MapDiff"/>s</param>
		/// <param name="baseUrl">The base URL of the request</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation</param>
		/// <returns>A <see cref="Task"/> representing the running operation</returns>
		async Task HandleResults(PullRequest pullRequest, List<MapDiff> diffResults, string baseUrl, CancellationToken cancellationToken)
		{
			int formatterCount = 0;
			
			var databaseContext = serviceProvider.GetRequiredService<IDatabaseContext>();

			//delete outdated renderings if neccessary
			var deleteTask = databaseContext.MapDiffs.Where(x => x.RepositoryId == pullRequest.Base.Repository.Id && x.PullRequestNumber == pullRequest.Number).DeleteAsync(cancellationToken);

			var commentBuilder = new StringBuilder();
			var prefix = String.Concat("https://", baseUrl);
			foreach (var I in diffResults)
			{
				var beforeUrl = String.Concat(prefix, FilesController.RouteTo(pullRequest, formatterCount, "before"));
				var afterUrl = String.Concat(prefix, FilesController.RouteTo(pullRequest, formatterCount, "after"));
				var logsUrl = String.Concat(prefix, FilesController.RouteTo(pullRequest, formatterCount, "logs"));

				commentBuilder.Append(String.Format(CultureInfo.InvariantCulture,
					"<details><summary>{0}</summary><br>{1} | {2}<br>--- | ---<br>![]({3}) | ![]({4})<br><details><summary>{5}</summary><br>{6} | {7} | {8}<br>--- | --- | ---<br>{9} | {10} | ![{8}]({11})</details></details><br>",
					I.MapPath,
					stringLocalizer["Old"],
					stringLocalizer["New"],
					beforeUrl,
					afterUrl,
					stringLocalizer["Details"],
					stringLocalizer["Status"],
					stringLocalizer["Region"],
					stringLocalizer["Logs"],
					I.BeforeImage != null ? (I.AfterImage != null ? stringLocalizer["Modified"] : stringLocalizer["Deleted"]) : stringLocalizer["Created"],
					I.MapRegion?.ToString() ?? stringLocalizer["ALL"],
					logsUrl
					));
				databaseContext.MapDiffs.Add(I);
				++formatterCount;
			}
			
			var comment = String.Format(CultureInfo.CurrentCulture, "{0}<br>{1}<br>{2}", commentBuilder, stringLocalizer["Last updated from merging commit {0} into {1}", pullRequest.Head.Sha, pullRequest.Base.Sha], stringLocalizer["Full job logs available [here]({0})", String.Concat(prefix, FilesController.RouteToLogs(pullRequest))]);

			await deleteTask.ConfigureAwait(false);
			await databaseContext.Save(cancellationToken).ConfigureAwait(false);
			await serviceProvider.GetRequiredService<IGitHubManager>().CreateSingletonComment(pullRequest, comment, cancellationToken).ConfigureAwait(false);
		}

		public void ProcessPayload(PullRequestEventPayload payload, IUrlHelper urlHelper)
		{
			if (payload.Action == "opened" || payload.Action == "synchronize")
			{
				var basePath = urlHelper.ActionContext.HttpContext.Request.Host + urlHelper.ActionContext.HttpContext.Request.PathBase;
				backgroundJobClient.Enqueue(() => ScanPullRequest(new JobSubmission(payload.PullRequest, basePath, stringLocalizer), JobCancellationToken.Null));
			}
		}
		public void ProcessPayload(IssueCommentPayload payload, IUrlHelper urlHelper)
		{
			if (payload.Action != "created" || payload.Comment.Body == null)
				return;
			if (payload.Comment.Body.Split(' ').Any(x => x == String.Format(CultureInfo.InvariantCulture, "@{0}", gitHubConfiguration.TagUser)))
			{
				var basePath = urlHelper.ActionContext.HttpContext.Request.Host + urlHelper.ActionContext.HttpContext.Request.PathBase;
				backgroundJobClient.Enqueue(() => ScanPullRequest(new JobSubmission(payload.Issue, payload.Repository, basePath, stringLocalizer), JobCancellationToken.Null));
			}
		}
	}
}
