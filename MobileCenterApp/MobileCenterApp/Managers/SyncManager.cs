﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace MobileCenterApp
{
	public class SyncManager
	{
		public static SyncManager Shared { get; set; } = new SyncManager();
		public MobileCenterApi.MobileCenterAPIServiceApiKeyApi Api { get; set; } = new MobileCenterApi.MobileCenterAPIServiceApiKeyApi("MobileCenter", "Ttw8AMUjYeEkr==");
		public SyncManager()
		{
			#if DEBUG
			Api.Verbose = true;
			#endif
		}
		Task syncAppsTask;
		public Task SyncApps()
		{
			if (syncAppsTask?.IsCompleted ?? true)
				syncAppsTask = syncApps();
			return syncAppsTask;
		}

		async Task syncApps()
		{
			if (Settings.IsOfflineMode)
				return;
			var apps = await Api.GetApps();
			List<Owner> owners = new List<Owner>();
			List<AppClass> myApps = new List<AppClass>();

			apps.ToList().ForEach(x =>
			{
				myApps.Add((AppClass)x.ToAppClass());
				owners.Add((Owner)x.Owner.ToAppOwner());
			});

			await Database.Main.ResetTable<AppClass>();
			await Database.Main.InsertAllAsync(myApps);
			var distintOwners = owners.DistinctBy(x => x.Id).ToList();
			Database.Main.InsertOrReplaceAll(distintOwners);
			Database.Main.ClearMemory();
			NotificationManager.Shared.ProcAppsChanged();
		}

		Task<User> userTask;
		public Task<User> GetUser()
		{
			if (userTask?.IsCompleted ?? true)
				userTask = Task.Run(async () =>
				{
					var profile = await Api.GetUserProfile();
					var user = new User
					{
						AvatarUrl = profile.AvatarUrl,
						DisplayName = profile.DisplayName,
						CanChangePassword = profile.CanChangePassword,
						Email = profile.Email,
						Id = profile.Id,
						Name = profile.Name,
					};
					Settings.CurrentUser = user;
					return user;
				});
			return userTask;
		}

		public async Task<bool> CreateApp(AppClass app)
		{
			try
			{
				var resp = await Api.PostCreateApp(app.ToAppRequest());
				var newApp = resp.ToAppClass();
				var owner = resp.Owner.ToAppOwner();
				Database.Main.InsertOrReplace(newApp);
				Database.Main.InsertOrReplace(owner);
				Database.Main.ClearMemory<AppClass>();
				NotificationManager.Shared.ProcAppsChanged();
				return true;
			}
			catch (Exception ex)
			{
				if (ex.Data.Contains("HttpContent"))
				{
					Console.WriteLine(ex.Data["HttpContent"]);
				}
				Console.WriteLine(ex);
			}
			return false;
		}
		public async Task<bool> DeleteApp(AppClass app)
		{
			try
			{
				await Api.DeleteApp(app.Name,app.Owner.Name);
				Database.Main.Delete(app);
				Database.Main.ClearMemory<AppClass>();
				NotificationManager.Shared.ProcAppsChanged();
				return true;
			}
			catch (Exception ex)
			{
				if (ex.Data.Contains("HttpContent"))
				{
					Console.WriteLine(ex.Data["HttpContent"]);
				}
				else
					Console.WriteLine(ex);
			}
			return false;
		}

		Dictionary<string, Task> syncbranchesTask = new Dictionary<string, Task>();
		public Task SyncBranch(AppClass app)
		{
			Task syncBranchTask;
			syncbranchesTask.TryGetValue(app.Id, out syncBranchTask);
			if (syncBranchTask?.IsCompleted ?? true)
				syncBranchTask = syncBranch(app);
			return syncBranchTask;
		}

		async Task syncBranch(AppClass app)
		{
			var branchStatus = await Api.BuildGetBranches(app.Owner.Name, app.Name);

			var branches  = new List<Branch>();
			var commits = new List<CommitClass>();
			var builds = new List<Build>();
			branchStatus.ToList().ForEach(x =>
			{
				branches.Add(x.ToBranch(app.Id));
				if(x?.Branch?.Commit != null)
					commits.Add(x.Branch.Commit.ToCommit(app.Id));
				if (x?.LastBuild != null)
					builds.Add(x.LastBuild.ToBuild(app.Id));
			});
			await Database.Main.ExecuteAsync("delete from Branch where AppId = ?", app.Id);
			await Database.Main.InsertAllAsync(branches);
			var distinctCommits = commits.DistinctBy(x => x.Sha).ToList();
			Database.Main.InsertOrReplaceAll(distinctCommits);
			var distinctBuilds = builds.DistinctBy(x => x.Id).ToList();
			Database.Main.InsertOrReplaceAll(distinctBuilds);
			Database.Main.ClearMemory();
			NotificationManager.Shared.ProcBranchesChanged(app.Id);
			syncbranchesTask.Remove(app.Id);
		}


		Dictionary<string, Task> syncRepoConfigTasks = new Dictionary<string, Task>();
		public Task SyncRepoConfig(AppClass app)
		{
			Task syncRepoConfigTask;
			syncRepoConfigTasks.TryGetValue(app.Id, out syncRepoConfigTask);
			if (syncRepoConfigTask?.IsCompleted ?? true)
				syncRepoConfigTask = syncRepoConfig(app);
			return syncRepoConfigTask;
		}

		async Task syncRepoConfig(AppClass app)
		{
			var config = await Api.BuildGetRepositoryConfiguration(app.Owner.Name, app.Name,true).ConfigureAwait(false);
			Database.Main.InsertOrReplace(config.ToRepoConfig(app.Id));
			Database.Main.ClearMemory();
			syncRepoConfigTasks.Remove(app.Id);
		}


		Dictionary<string, Task> syncBuildsTasks = new Dictionary<string, Task>();
		public Task SyncBuilds(Branch branch)
		{
			Task syncBuildsTask;
			syncBuildsTasks.TryGetValue(branch.Id, out syncBuildsTask);
			if (syncBuildsTask?.IsCompleted ?? true)
				syncBuildsTask = syncBranches(branch);
			return syncBuildsTask;
		}

		async Task syncBranches(Branch branch)
		{
			var app = branch.App;
			var builds = await Api.BuildGetBranchBuilds(branch.Name, app.Owner.Name, app.Name).ConfigureAwait(false);
			var myBuilds = builds.Select(x => x.ToBuild(app.Id)).ToList();
			await Database.Main.ExecuteAsync("delete from Build where AppId = ? and SourceBranch = ?", app.Id, branch.Name);

			Database.Main.InsertOrReplaceAll(myBuilds);
			//Hopefully we can remove this nonsense later.
			var missingShas = myBuilds.Where(x => x.LastCommit == null || string.IsNullOrWhiteSpace(x.LastCommit.Message)).Select(x=> x.SourceVersion).ToList();
			if (missingShas.Any())
			{
				var shaString = string.Join(",", missingShas);
				var commits = await Api.GetCommits(shaString, app.Owner.Name, app.Name,"full");
				if (commits.Any())
				{
					Database.Main.InsertOrReplaceAll(commits.Select(x => x.ToCommit(app.Id)));
				}
			}
			syncBuildsTasks.Remove(branch.Id);
		}
	}
}
