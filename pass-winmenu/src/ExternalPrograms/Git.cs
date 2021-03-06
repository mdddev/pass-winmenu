﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using LibGit2Sharp;

namespace PassWinmenu.ExternalPrograms
{
	/// <inheritdoc cref="IDisposable" />
	/// <summary>
	/// Simple wrapper over git.
	/// </summary>
	internal class Git : IDisposable, ISyncService
	{
		private readonly Repository repo;
		private readonly FetchOptions fetchOptions;
		private readonly PushOptions pushOptions;
		private readonly string repositoryPath;
		private readonly string nativeGitPath;
		private readonly TimeSpan gitCallTimeout = TimeSpan.FromSeconds(5);

		/// <summary>
		/// Initialises the wrapper.
		/// </summary>
		/// <param name="repositoryPath">The repository git should operate on.</param>
		public Git(string repositoryPath)
		{
			this.repositoryPath = repositoryPath;
			repo = new Repository(repositoryPath);
			fetchOptions = new FetchOptions();
			pushOptions = new PushOptions();
		}

		/// <summary>
		/// Initialises the wrapper, using a native Git installation for pushing/pulling.
		/// </summary>
		/// <param name="repositoryPath">The repository git should operate on.</param>
		/// <param name="nativeGitPath">The path to the Git executable.</param>
		public Git(string repositoryPath, string nativeGitPath) : this(repositoryPath)
		{
			this.nativeGitPath = nativeGitPath ?? throw new ArgumentNullException(nameof(nativeGitPath));
		}

		public BranchTrackingDetails GetTrackingDetails() => repo.Head.TrackingDetails;

		private Signature BuildSignature()
		{
			var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
			if(sig == null){
				throw new GitException("Could not build Git signature. Make sure 'user.name' and 'user.email' are configured for the repository.");
			}
			return sig;
		}
		

		/// <summary>
		/// Rebases the current branch onto the branch it is tracking.
		/// </summary>
		public void Rebase()
		{
			var head = repo.Head;
			var tracked = head.TrackedBranch;

			var sig = BuildSignature();

			var result = repo.Rebase.Start(head, tracked, null, new Identity(sig.Name, sig.Email), new RebaseOptions());
			if (result.Status != RebaseStatus.Complete)
			{
				repo.Rebase.Abort();
				throw new GitException($"Could not rebase {head.FriendlyName} onto {head.TrackedBranch.FriendlyName}");
			}
			else if (result.CompletedStepCount > 0)
			{
				// One or more commits were rebased
			}
			else
			{
				// Fast-forward or no upstream changes
			}
		}

		private void CallGit(string arguments)
		{
			var argList = new List<string>
			{
				// May be required in certain cases?
				//"--non-interactive"
			};

			var psi = new ProcessStartInfo
			{
				FileName = nativeGitPath,
				WorkingDirectory = repositoryPath,
				Arguments = $"{arguments} {string.Join(" ", argList)}",
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				CreateNoWindow = true
			};
			if (!String.IsNullOrEmpty(Configuration.ConfigManager.Config.Git.SshPath))
			{
				psi.EnvironmentVariables.Add("GIT_SSH", Configuration.ConfigManager.Config.Git.SshPath);
			}
			Process gitProc;
			try
			{
				gitProc = Process.Start(psi);
			}
			catch (Win32Exception e)
			{
				throw new GitException("Git failed to start. " + e.Message, e);
			}

			gitProc.WaitForExit((int)gitCallTimeout.TotalMilliseconds);
			var output = gitProc.StandardOutput.ReadToEnd();
			var error = gitProc.StandardError.ReadToEnd();
			if (gitProc.ExitCode != 0)
			{
				throw new GitException($"Git exited with code {gitProc.ExitCode}", error);
			}
		}

		public void Fetch()
		{
			var head = repo.Head;
			var remote = repo.Network.Remotes[head.RemoteName];

			if (nativeGitPath == null)
			{
				Commands.Fetch(repo, head.RemoteName, remote.FetchRefSpecs.Select(rs => rs.Specification), fetchOptions, null);
			}
			else
			{
				CallGit("fetch " + remote.Name);
			}
		}

		/// <summary>
		/// Pushes changes to remote.
		/// </summary>
		public void Push()
		{
			if (nativeGitPath == null)
			{
				repo.Network.Push(repo.Head, pushOptions);
			}
			else
			{
				CallGit("push");
			}
		}

		private void UnstageAll()
		{
			var status = repo.RetrieveStatus();
			var staged = status.Where(e => (e.State
			                                & (FileStatus.DeletedFromIndex
			                                   | FileStatus.ModifiedInIndex
			                                   | FileStatus.NewInIndex
			                                   | FileStatus.RenamedInIndex
			                                   | FileStatus.TypeChangeInIndex)) > 0)
			                   .ToList();
			if (staged.Any())
			{
				Commands.Unstage(repo, staged.Select(entry => entry.FilePath));
			}
		}

		public RepositoryStatus Commit()
		{
			UnstageAll();

			var status = repo.RetrieveStatus();
			var filesToCommit = status.Where(f => f.State != FileStatus.Ignored);

			foreach (var entry in filesToCommit)
			{
				Commands.Stage(repo, entry.FilePath);
				var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
				repo.Commit($"{GetVerbFromGitFileStatus(entry.State)} password store file {entry.FilePath}\n\n" +
							"This commit was automatically generated by pass-winmenu.", sig, sig);
			}

			return status;
		}

		private string GetVerbFromGitFileStatus(FileStatus status)
		{
			switch (status)
			{
				case FileStatus.DeletedFromWorkdir:
					return "Delete";
				case FileStatus.NewInWorkdir:
					return "Add";
				case FileStatus.ModifiedInWorkdir:
					return "Modify";
				case FileStatus.RenamedInWorkdir:
					return "Rename";
				case FileStatus.TypeChangeInWorkdir:
					return "Change filetype for";
				default:
					throw new ArgumentException(nameof(status));
			}
		}

		public void EditPassword(string passwordFilePath)
		{
			var status = repo.RetrieveStatus(passwordFilePath);
			if (status == FileStatus.ModifiedInWorkdir)
			{
				Commands.Stage(repo, passwordFilePath);
				var sig = BuildSignature();
				repo.Commit($"Edit password file {passwordFilePath}\n\n" +
							"This commit was automatically generated by pass-winmenu.", sig, sig);
			}
		}

		public void AddPassword(string passwordFilePath)
		{
			var status = repo.RetrieveStatus(passwordFilePath);
			if (status == FileStatus.NewInWorkdir)
			{
				Commands.Stage(repo, passwordFilePath);
				var sig = BuildSignature();
				repo.Commit($"Add password file {passwordFilePath}\n\n" +
							"This commit was automatically generated by pass-winmenu.", sig, sig);
			}
		}

		public void Dispose()
		{
			repo?.Dispose();
		}
	}

	[Serializable]
	internal class GitException : Exception
	{
		public string GitError { get; }

		public GitException(string message) : base(message)
		{
		}

		public GitException(string message, Exception innerException) : base(message, innerException)
		{
		}

		public GitException(string message, string gitError) : base(message)
		{
			GitError = gitError;
		}
	}
}
