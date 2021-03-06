﻿using System.Collections.Generic;
using Rainbow;
using Rainbow.Model;
using Sitecore.Configuration;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.StringExtensions;
using Unicorn.Configuration;
using Unicorn.Data;
using Unicorn.UI.Pipelines.GetContentEditorWarnings;

namespace Unicorn.Evaluators
{
	/// <summary>
	/// Evaluates to add new items only. Existing or orphaned items are left alone.
	/// NOTE: Using transparent sync with this evaluator is not a good idea, because the semantics of transparent sync are to read from the serialization store directly.
	/// In other words, transparent sync always acts like SerializedAsMasterEvaluator, and because there is no sync invocation the evaluator is not invoked.
	/// </summary>
	public class NewItemOnlyEvaluator : IEvaluator, IDocumentable
	{
		private readonly INewItemOnlyEvaluatorLogger _logger;
		private readonly ISourceDataStore _sourceDataStore;
		private readonly ITargetDataStore _targetDataStore;
		private readonly IConfiguration _parentConfiguration;

		public NewItemOnlyEvaluator(INewItemOnlyEvaluatorLogger logger, ISourceDataStore sourceDataStore, ITargetDataStore targetDataStore, IConfiguration parentConfiguration)
		{
			Assert.ArgumentNotNull(logger, "logger");
			Assert.ArgumentNotNull(sourceDataStore, "sourceDataStore");
			Assert.ArgumentNotNull(targetDataStore, "targetDataStore");
			Assert.ArgumentNotNull(parentConfiguration, "parentConfiguration");

			_logger = logger;
			_sourceDataStore = sourceDataStore;
			_targetDataStore = targetDataStore;
			_parentConfiguration = parentConfiguration;
		}

		public virtual void EvaluateOrphans(IItemData[] orphanItems)
		{
			Assert.ArgumentNotNull(orphanItems, "orphanItems");
			foreach(var orphan in orphanItems) _logger.Evaluated(orphan);
		}

		public virtual IItemData EvaluateNewSerializedItem(IItemData newItemData)
		{
			Assert.ArgumentNotNull(newItemData, "newItem");

			_logger.DeserializedNewItem(newItemData);

			_sourceDataStore.Save(newItemData);

			_logger.Evaluated(newItemData);

			return newItemData;
		}

		public virtual IItemData EvaluateUpdate(IItemData sourceItem, IItemData targetItem)
		{
			Assert.ArgumentNotNull(sourceItem, "sourceItemData");
			_logger.Evaluated(sourceItem);

			return null;
		}

		public virtual Warning EvaluateEditorWarning(Item item)
		{
			// if dev mode is on, we don't need a warning
			if (Settings.GetBoolSetting("Unicorn.DevMode", true))
				return new Warning("This item is part of a Unicorn deploy once configuration", "Changes to this item will not be synced to other environments unless the item needs to be created. Configuration: '{0}'".FormatWith(_parentConfiguration.Name));

			var existingTargetItem = _targetDataStore.GetByPathAndId(item.Paths.Path, item.ID.Guid, item.Database.Name);

			// if we have no existing serialized item, there's no need for a warning: Unicorn won't touch this item when using NIO
			if (existingTargetItem == null) return null;

			return new Warning("This item was created by Unicorn.".FormatWith(_parentConfiguration.Name), "You may edit this item, but deleting it may result in its return next time code is deployed. Ask a developer to help if you need to delete this item. Configuration: '{0}'".FormatWith(_parentConfiguration.Name));
		}

		public virtual bool ShouldPerformConflictCheck(Item item)
		{
			// we don't care about conflicts because items may be edited after creation and are expected to not necessarily match serialized versions
			return false;
		}

		public virtual string FriendlyName
		{
			get { return "New Item Only Evaluator"; }
		}

		public virtual string Description
		{
			get { return "During a sync only items that are not already in the Sitecore database are synced. If an item already exists, it is never modified. Useful for deploying items only once and leaving them alone from then on."; }
		}

		public virtual KeyValuePair<string, string>[] GetConfigurationDetails()
		{
			return new KeyValuePair<string, string>[0];
		}
	}
}
