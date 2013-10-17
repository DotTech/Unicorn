﻿using System;
using System.Linq;
using Sitecore;
using Sitecore.Data;
using Sitecore.Data.DataProviders;
using Sitecore.Data.Items;
using Sitecore.Data.Serialization;
using Sitecore.Diagnostics;
using Sitecore.Globalization;
using Unicorn.Data;
using Unicorn.Predicates;
using Unicorn.Serialization;

namespace Unicorn
{
	public class UnicornDataProvider
	{
		private readonly ISerializationProvider _serializationProvider;
		private readonly IPredicate _predicate;
		private readonly IUnicornDataProviderLogger _logger;
		[ThreadStatic]
		private static bool _disableSerialization;

		public UnicornDataProvider(ISerializationProvider serializationProvider, IPredicate predicate, IUnicornDataProviderLogger logger)
		{
			_logger = logger;
			_predicate = predicate;
			_serializationProvider = serializationProvider;
		}

		/// <summary>
		/// Disables all serialization handling if true. Used during serialization load tasks.
		/// </summary>
		public static bool DisableSerialization
		{
			get
			{
				// we have to check standard serialization's disabled attribute as well,
				// because if we do not serialization operations in the content editor will clash with ours
				if (ItemHandler.DisabledLocally) return true; 
				return _disableSerialization;
			}
			set { _disableSerialization = value; }
		}

		public DataProvider DataProvider { get; set; }
		protected Database Database { get { return DataProvider.Database; } }

		public void CreateItem(ID itemId, string itemName, ID templateId, ItemDefinition parent, CallContext context)
		{
			if (DisableSerialization) return;

			// TODO: do we need to handle this? (if so we need a way to create an ISerializedItem from scratch...)
		}

		public void SaveItem(ItemDefinition itemDefinition, ItemChanges changes, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");
			Assert.ArgumentNotNull(changes, "changes");

			var sourceItem = new SitecoreSourceItem(changes.Item);

			if (!_predicate.Includes(sourceItem).IsIncluded) return;

			string oldName = changes.Renamed ? changes.Properties["name"].OriginalValue.ToString() : string.Empty;
			if (changes.Renamed && !oldName.Equals(sourceItem.Name, StringComparison.Ordinal)) // it's a rename, in which the name actually changed (template builder will cause 'renames' for the same name!!!)
			{
				_logger.RenamedItem(_serializationProvider.LogName, sourceItem, oldName);
				_serializationProvider.RenameSerializedItem(sourceItem, oldName);
			}
			else if (HasConsequentialChanges(changes)) // it's a simple update - but we reject it if only inconsequential fields (last updated, revision) were changed - again, template builder FTW
			{
				_serializationProvider.SerializeItem(sourceItem);
				_logger.SavedItem(_serializationProvider.LogName, sourceItem);
			}
		}

		public void MoveItem(ItemDefinition itemDefinition, ItemDefinition destination, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			var sourceItem = GetSourceFromDefinition(itemDefinition);
			var destinationItem = GetSourceFromDefinition(destination);

			if (!_predicate.Includes(destinationItem).IsIncluded) // if the destination we are moving to is NOT included for serialization, we delete the existing item
			{
				var existingItem = GetExistingSerializedItem(sourceItem.Id);

				if (existingItem != null)
				{
					_serializationProvider.DeleteSerializedItem(existingItem);
					_logger.MovedItemToNonIncludedLocation(_serializationProvider.LogName, existingItem);
				}

				return;
			}

			_serializationProvider.MoveSerializedItem(sourceItem, destinationItem);
			_logger.MovedItem(_serializationProvider.LogName, sourceItem, destinationItem);
		}

		public void CopyItem(ItemDefinition source, ItemDefinition destination, string copyName, ID copyID, CallContext context)
		{
			if (DisableSerialization) return;

			// copying is easy - all we have to do is serialize the copyID. Copied children will all result in multiple calls to CopyItem so we don't even need to worry about them.
			var copiedItem = new SitecoreSourceItem(Database.GetItem(copyID));

			if (!_predicate.Includes(copiedItem).IsIncluded) return; // destination parent is not in a path that we are serializing, so skip out

			_serializationProvider.SerializeItem(copiedItem);
			_logger.CopiedItem(_serializationProvider.LogName, () => GetSourceFromDefinition(source), copiedItem);
		}

		public void AddVersion(ItemDefinition itemDefinition, VersionUri baseVersion, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			SerializeItemIfIncluded(itemDefinition);
		}

		public void DeleteItem(ItemDefinition itemDefinition, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			var existingItem = GetExistingSerializedItem(itemDefinition.ID);

			if (existingItem == null) return; // it was already gone or an item from a different data provider

			_serializationProvider.DeleteSerializedItem(existingItem);
			_logger.DeletedItem(_serializationProvider.LogName, existingItem);
		}

		public void RemoveVersion(ItemDefinition itemDefinition, VersionUri version, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			SerializeItemIfIncluded(itemDefinition);
		}

		public void RemoveVersions(ItemDefinition itemDefinition, Language language, bool removeSharedData, CallContext context)
		{
			if (DisableSerialization) return;

			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			SerializeItemIfIncluded(itemDefinition);
		}

		protected virtual bool SerializeItemIfIncluded(ItemDefinition itemDefinition)
		{
			Assert.ArgumentNotNull(itemDefinition, "itemDefinition");

			var sourceItem = GetSourceFromDefinition(itemDefinition);

			if (!_predicate.Includes(sourceItem).IsIncluded) return false; // item was not included so we get out

			_serializationProvider.SerializeItem(sourceItem);
			_logger.SavedItem(_serializationProvider.LogName, sourceItem);

			return true;
		}

		protected virtual ISerializedItem GetExistingSerializedItem(ID id)
		{
			Assert.ArgumentNotNullOrEmpty(id, "id");

			var item = Database.GetItem(id);

			if (item == null) return null;

			var reference = _serializationProvider.GetReference(new SitecoreSourceItem(item));

			if (reference == null) return null;

			return _serializationProvider.GetItem(reference);
		}

		protected virtual ISourceItem GetSourceFromDefinition(ItemDefinition definition)
		{
			return new SitecoreSourceItem(Database.GetItem(definition.ID));
		}

		protected virtual bool HasConsequentialChanges(ItemChanges changes)
		{
			foreach (FieldChange change in changes.FieldChanges)
			{
				if (change.OriginalValue == change.Value) continue;
				if (change.FieldID == FieldIDs.Revision) continue;
				if (change.FieldID == FieldIDs.Updated) continue;

				return true;
			}

			_logger.SaveRejectedAsInconsequential(_serializationProvider.LogName, changes);

			return false;
		}
	}
}