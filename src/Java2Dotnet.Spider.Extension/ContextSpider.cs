﻿using System;
using System.Collections.Generic;
using System.Threading;
using Java2Dotnet.Spider.Core;
using Java2Dotnet.Spider.Core.Downloader;
using Java2Dotnet.Spider.Core.Scheduler;
using Java2Dotnet.Spider.Extension.Configuration;
using Java2Dotnet.Spider.Extension.Downloader;
using Java2Dotnet.Spider.Extension.Downloader.WebDriver;
using Java2Dotnet.Spider.Extension.Model;
using Java2Dotnet.Spider.Extension.Monitor;
using Java2Dotnet.Spider.Extension.ORM;
using Java2Dotnet.Spider.Extension.Pipeline;
using Java2Dotnet.Spider.Extension.Processor;
using Java2Dotnet.Spider.Extension.Utils;
using Java2Dotnet.Spider.Common;
using Java2Dotnet.Spider.Redial;
using Java2Dotnet.Spider.Redial.NetworkValidater;
using Java2Dotnet.Spider.Redial.Redialer;
using Java2Dotnet.Spider.Redial.RedialManager;
using Java2Dotnet.Spider.Validation;
using Java2Dotnet.Spider.JLog;
using Newtonsoft.Json.Linq;
using System.Linq;
using RedisSharp;
using DefaultNetworkValidater = Java2Dotnet.Spider.Redial.NetworkValidater.DefaultNetworkValidater;
using VpsNetworkValidater = Java2Dotnet.Spider.Redial.NetworkValidater.VpsNetworkValidater;
using static Java2Dotnet.Spider.Extension.Monitor.SpiderMonitor;

namespace Java2Dotnet.Spider.Extension
{
	public class ContextSpider
	{
		private const string InitStatusSetName = "init-status";
		private const string ValidateStatusName = "validate-status";
		protected static readonly ILog _logger = LogManager.GetLogger();
 
		private readonly string _validateReportTo;

		private readonly List<IValidate> _validations = new List<IValidate>();
		private readonly SpiderContext _spiderContext;
		private RedisServer redis;
		public string Name { get; }

		public ContextSpider(SpiderContext spiderContext)
		{
			_spiderContext = spiderContext;

			_validateReportTo = _spiderContext.ValidationReportTo;
			if (!string.IsNullOrEmpty(_validateReportTo))
			{
				CheckValidations();
			}

			Name = _spiderContext.SpiderName;

			InitEnvoriment();
		}

		private void InitEnvoriment()
		{
			redis = new RedisServer(ConfigurationManager.Get("redisHost"), 6379, ConfigurationManager.Get("redisPassword"));

			if (_spiderContext.Redialer != null)
			{
				//RedialManagerUtils.RedialManager = FileLockerRedialManager.Default;
				RedialManagerUtils.RedialManager = new RedisRedialManager();

				RedialManagerUtils.RedialManager.NetworkValidater = GetNetworValidater(_spiderContext.NetworkValidater);
				RedialManagerUtils.RedialManager.Redialer = _spiderContext.Redialer.GetRedialer();
			}

			if (_spiderContext.Downloader == null)
			{
				_spiderContext.Downloader = new HttpDownloader();
			}
		}

		public void Run(params string[] args)
		{
			Core.Spider spider = null;
			try
			{
				spider = PrepareSpider(args);
				spider?.Run();

				RunAfterSpiderFinished();

				if (!string.IsNullOrEmpty(_validateReportTo))
				{
					DoValidate();
				}
			}
			finally
			{
				MonitorSpiderListener.WaitForExit();
				spider?.Dispose();
			}
		}

		private void CheckValidations()
		{
			if (_validations != null && _validations.Count > 0)
			{
				foreach (var validation in _validations)
				{
					validation.CheckArguments();
				}
			}
		}

		private void DoValidate()
		{
			string key = "locker-validate-" + Name;
			try
			{
				_logger.Info($"Lock: {key} to keep only one validate process.");

				while (!redis.LockTake(key, "0", TimeSpan.FromMinutes(10)))
				{
					Thread.Sleep(1000);
				}

				var lockerValue = redis.HashGet(ValidateStatusName, Name).ToString();
				bool needInitStartRequest = lockerValue != "validate finished";

				if (needInitStartRequest)
				{
					_logger.Info("Start validate ...");

					if (_validations != null && _validations.Count > 0)
					{
						MailBodyBuilder builder = new MailBodyBuilder(Name,
#if !NET_CORE
							System.Configuration.ConfigurationManager.AppSettings["corporation"]
#else
							"ooodata.com"
#endif
							);
						foreach (var validation in _validations)
						{
							builder.AddValidateResult(validation.Validate());
						}
						string mailBody = builder.Build();

#if !NET_CORE
						EmailUtil.Send($"{Name} " + "validation report", _validateReportTo, mailBody);
#endif
					}
				}
				else
				{
					_logger.Info("No need to validate on this process because other process did.");
				}

				if (needInitStartRequest)
				{
					redis.HashSet(ValidateStatusName, Name, "validate finished");
				}
			}
			catch (Exception e)
			{
				_logger.Error(e.Message, e);
			}
			finally
			{
				_logger.Info("Release locker.");

				redis.LockRelease(key, 0);
			}
		}

		private Core.Spider PrepareSpider(params string[] args)
		{
			_logger.Info($"Spider Name Md5Encrypt: {Encrypt.Md5Encrypt(Name)}");

			var schedulerType = _spiderContext.Scheduler.Type;

			switch (schedulerType)
			{
				case Configuration.Scheduler.Types.Queue:
					{
						PrepareSite();
						var spider = GenerateSpider(_spiderContext.Scheduler.GetScheduler());
						spider.InitComponent();
						return spider;

					}
				case Configuration.Scheduler.Types.Redis:
					{

						var scheduler = (Scheduler.RedisScheduler)(_spiderContext.Scheduler.GetScheduler());

						string key = "locker-" + Name;
						if (args != null && args.Length > 0)
						{
							if (args.Contains("rerun"))
							{
								_logger.Info($"Starting execute command: rerun");

								redis.KeyDelete(Scheduler.RedisScheduler.GetQueueKey(Name));
								redis.KeyDelete(Scheduler.RedisScheduler.GetSetKey(Name));
								redis.HashDelete(Scheduler.RedisScheduler.TaskStatus, Name);
								redis.KeyDelete(Scheduler.RedisScheduler.ItemPrefix + Name);
								redis.KeyDelete(Name);
								redis.KeyDelete(key);
								redis.SortedSetRemove(Scheduler.RedisScheduler.TaskList, Name);
								redis.HashDelete("init-status", Name);
								redis.HashDelete("validate-status", Name);
								redis.KeyDelete("set-" + Encrypt.Md5Encrypt(Name));
								_logger.Info($"Execute command: rerun finished.");
							}
							if (args.Contains("noconsole"))
							{
								Log.WriteLine("No console log info.");
								Log.NoConsole = true;
							}
						}

						try
						{
							_logger.Info($"Lock: {key} to keep only one prepare process.");
							while (!redis.LockTake(key, "0", TimeSpan.FromMinutes(10)))
							{
								Thread.Sleep(1000);
							}

							var lockerValue = redis.HashGet(InitStatusSetName, Name);
							bool needInitStartRequest = lockerValue != "init finished";

							if (needInitStartRequest)
							{
								_logger.Info("Preparing site...");

								PrepareSite();
							}
							else
							{
								_logger.Info("No need to prepare site because other process did it.");
								_spiderContext.Site.ClearStartRequests();
							}

							_logger.Info("Start creating Spider...");

							var spider = GenerateSpider(scheduler);

							_logger.Info("Creat spider finished.");

							spider.SaveStatus = true;
							SpiderMonitor.Default.Register(spider);

							_logger.Info("Start init component...");
							spider.InitComponent();
							_logger.Info("Init component finished.");

							if (needInitStartRequest)
							{
								redis.HashSet(InitStatusSetName, Name, "init finished");
							}

							_logger.Info("Creating Spider finished.");

							return spider;
						}
						catch (Exception e)
						{
							_logger.Error(e.Message, e);
							return null;
						}
						finally
						{
							_logger.Info("Release locker.");
							try
							{
								redis.LockRelease(key, 0);
							}
							catch
							{
								// ignored
							}
						}
					}
			}

			throw new SpiderExceptoin("Prepare spider failed.");
		}

		private void PrepareSite()
		{
			if (_spiderContext.PrepareStartUrls != null)
			{
				foreach (var prepareStartUrl in _spiderContext.PrepareStartUrls)
				{
					prepareStartUrl.Build(_spiderContext.Site);
				}
			}
		}

		private Core.Spider GenerateSpider(IScheduler scheduler)
		{
			Site site = _spiderContext.Site;
			EntityProcessor processor = new EntityProcessor(_spiderContext);
			foreach (var entity in _spiderContext.Entities)
			{
				processor.AddEntity(entity);
			}

			EntityGeneralSpider spider = new EntityGeneralSpider(_spiderContext.SpiderName, processor, scheduler);

			foreach (var entity in _spiderContext.Entities)
			{
				string entiyName = entity.SelectToken("$.Identity")?.ToString();

				var schema = entity.SelectToken("$.Schema")?.ToObject<Schema>();

				switch (_spiderContext.Pipeline.Type)
				{
					case Configuration.Pipeline.Types.MongoDb:
						{

							spider.AddPipeline(new EntityPipeline(entiyName, _spiderContext.Pipeline.GetPipeline(schema, entity)));

							break;
						}
					case Configuration.Pipeline.Types.MySql:
						{
							spider.AddPipeline(new EntityPipeline(entiyName, _spiderContext.Pipeline.GetPipeline(schema, entity)));
							break;
						}
					case Configuration.Pipeline.Types.MySqlFile:
						{
							spider.AddPipeline(new EntityPipeline(entiyName, _spiderContext.Pipeline.GetPipeline(schema, entity)));
							break;
						}
				}
			}
			spider.SetCachedSize(_spiderContext.CachedSize);
			spider.SetEmptySleepTime(_spiderContext.EmptySleepTime);
			spider.SetThreadNum(_spiderContext.ThreadNum);
			spider.Deep = _spiderContext.Deep;
			spider.SetDownloader(_spiderContext.Downloader.GetDownloader());

			if (_spiderContext.CustomizePage != null)
			{
				spider.CustomizePage = _spiderContext.CustomizePage.Customize;
			}

			if (_spiderContext.CustomizeTargetUrls != null)
			{
				spider.SetCustomizeTargetUrls(_spiderContext.CustomizeTargetUrls.Customize);
			}

			return spider;
		}

		protected void RunAfterSpiderFinished()
		{
		}

		private INetworkValidater GetNetworValidater(NetworkValidater networkValidater)
		{
			switch (networkValidater.Type)
			{
				case NetworkValidater.Types.Vps:
					{
						return new VpsNetworkValidater(((Configuration.VpsNetworkValidater)networkValidater).InterfaceNum);
					}
				case NetworkValidater.Types.Defalut:
					{
						return new Java2Dotnet.Spider.Redial.NetworkValidater.DefaultNetworkValidater();
					}
			}
			return null;
		}
	}
}
