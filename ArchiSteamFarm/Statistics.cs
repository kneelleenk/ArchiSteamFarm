﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
// Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Localization;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class Statistics : IDisposable {
		private const byte MaxMatchedBotsHard = 40;
		private const byte MaxMatchesBotsSoft = 20;
		private const byte MaxMatchingRounds = 10;
		private const byte MinAnnouncementCheckTTL = 6; // Minimum amount of hours we must wait before checking eligibility for Announcement, should be lower than MinPersonaStateTTL
		private const byte MinHeartBeatTTL = 10; // Minimum amount of minutes we must wait before sending next HeartBeat
		private const byte MinItemsCount = 100; // Minimum amount of items to be eligible for public listing
		private const byte MinPersonaStateTTL = 8; // Minimum amount of hours we must wait before requesting persona state update
		private const string URL = "https://" + SharedInfo.StatisticsServer;

		private static readonly HashSet<Steam.Asset.EType> AcceptedMatchableTypes = new HashSet<Steam.Asset.EType> {
			Steam.Asset.EType.Emoticon,
			Steam.Asset.EType.FoilTradingCard,
			Steam.Asset.EType.ProfileBackground,
			Steam.Asset.EType.TradingCard
		};

		private readonly Bot Bot;
		private readonly SemaphoreSlim MatchActivelySemaphore = new SemaphoreSlim(1, 1);
		private readonly Timer MatchActivelyTimer;
		private readonly SemaphoreSlim RequestsSemaphore = new SemaphoreSlim(1, 1);

		private DateTime LastAnnouncementCheck;
		private DateTime LastHeartBeat;
		private DateTime LastPersonaStateRequest;
		private bool ShouldSendHeartBeats;

		internal Statistics(Bot bot) {
			Bot = bot ?? throw new ArgumentNullException(nameof(bot));

			MatchActivelyTimer = new Timer(
				async e => await MatchActively().ConfigureAwait(false),
				null,
				TimeSpan.FromHours(1) + TimeSpan.FromSeconds(Program.LoadBalancingDelay * Bot.Bots.Count), // Delay
				TimeSpan.FromHours(8) // Period
			);
		}

		public void Dispose() {
			MatchActivelySemaphore.Dispose();
			MatchActivelyTimer.Dispose();
			RequestsSemaphore.Dispose();
		}

		internal async Task OnHeartBeat() {
			// Request persona update if needed
			if ((DateTime.UtcNow > LastPersonaStateRequest.AddHours(MinPersonaStateTTL)) && (DateTime.UtcNow > LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL))) {
				LastPersonaStateRequest = DateTime.UtcNow;
				Bot.RequestPersonaStateUpdate();
			}

			if (!ShouldSendHeartBeats || (DateTime.UtcNow < LastHeartBeat.AddMinutes(MinHeartBeatTTL))) {
				return;
			}

			await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (!ShouldSendHeartBeats || (DateTime.UtcNow < LastHeartBeat.AddMinutes(MinHeartBeatTTL))) {
					return;
				}

				const string request = URL + "/Api/HeartBeat";
				Dictionary<string, string> data = new Dictionary<string, string>(2) {
					{ "SteamID", Bot.SteamID.ToString() },
					{ "Guid", Program.GlobalDatabase.Guid.ToString("N") }
				};

				if (await Program.WebBrowser.UrlPost(request, data).ConfigureAwait(false) != null) {
					LastHeartBeat = DateTime.UtcNow;
				}
			} finally {
				RequestsSemaphore.Release();
			}
		}

		internal async Task OnLoggedOn() => await Bot.ArchiWebHandler.JoinGroup(SharedInfo.ASFGroupSteamID).ConfigureAwait(false);

		internal async Task OnPersonaState(string nickname = null, string avatarHash = null) {
			if (DateTime.UtcNow < LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL)) {
				return;
			}

			await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (DateTime.UtcNow < LastAnnouncementCheck.AddHours(MinAnnouncementCheckTTL)) {
					return;
				}

				// Don't announce if we don't meet conditions
				string tradeToken;
				if (!await IsEligibleForMatching().ConfigureAwait(false) || string.IsNullOrEmpty(tradeToken = await Bot.ArchiHandler.GetTradeToken().ConfigureAwait(false))) {
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = false;
					return;
				}

				HashSet<Steam.Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(type => AcceptedMatchableTypes.Contains(type)).ToHashSet();
				if (acceptedMatchableTypes.Count == 0) {
					Bot.ArchiLogger.LogNullError(nameof(acceptedMatchableTypes));
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = false;
					return;
				}

				HashSet<Steam.Asset> inventory = await Bot.ArchiWebHandler.GetInventory(Bot.SteamID, tradable: true, wantedTypes: acceptedMatchableTypes).ConfigureAwait(false);

				// This is actually inventory failure, so we'll stop sending heartbeats but not record it as valid check
				if (inventory == null) {
					ShouldSendHeartBeats = false;
					return;
				}

				// This is actual inventory
				if (inventory.Count < MinItemsCount) {
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = false;
					return;
				}

				const string request = URL + "/Api/Announce";
				Dictionary<string, string> data = new Dictionary<string, string>(9) {
					{ "SteamID", Bot.SteamID.ToString() },
					{ "Guid", Program.GlobalDatabase.Guid.ToString("N") },
					{ "Nickname", nickname ?? "" },
					{ "AvatarHash", avatarHash ?? "" },
					{ "GamesCount", inventory.Select(item => item.RealAppID).Distinct().Count().ToString() },
					{ "ItemsCount", inventory.Count.ToString() },
					{ "MatchableTypes", JsonConvert.SerializeObject(acceptedMatchableTypes) },
					{ "MatchEverything", Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) ? "1" : "0" },
					{ "TradeToken", tradeToken }
				};

				// Listing is free to deny our announce request, hence we don't retry
				if (await Program.WebBrowser.UrlPost(request, data, maxTries: 1).ConfigureAwait(false) != null) {
					LastAnnouncementCheck = DateTime.UtcNow;
					ShouldSendHeartBeats = true;
				}
			} finally {
				RequestsSemaphore.Release();
			}
		}

		private static async Task<HashSet<ListedUser>> GetListedUsers() {
			const string request = URL + "/Api/Bots";

			WebBrowser.ObjectResponse<HashSet<ListedUser>> objectResponse = await Program.WebBrowser.UrlGetToJsonObject<HashSet<ListedUser>>(request).ConfigureAwait(false);
			return objectResponse?.Content;
		}

		private async Task<bool> IsEligibleForMatching() {
			// Bot must have ASF 2FA
			if (!Bot.HasMobileAuthenticator) {
				return false;
			}

			// Bot must have STM enable in TradingPreferences
			if (!Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.SteamTradeMatcher)) {
				return false;
			}

			// Bot must have at least one accepted matchable type set
			if ((Bot.BotConfig.MatchableTypes.Count == 0) || Bot.BotConfig.MatchableTypes.All(type => !AcceptedMatchableTypes.Contains(type))) {
				return false;
			}

			// Bot must have public inventory
			if (!await Bot.ArchiWebHandler.HasPublicInventory().ConfigureAwait(false)) {
				return false;
			}

			// Bot must have valid API key (e.g. not being restricted account)
			return await Bot.ArchiWebHandler.HasValidApiKey().ConfigureAwait(false);
		}

		private async Task MatchActively() {
			if (!Bot.IsConnectedAndLoggedOn || Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) || !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively) || !await IsEligibleForMatching().ConfigureAwait(false)) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);
				return;
			}

			HashSet<Steam.Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(type => AcceptedMatchableTypes.Contains(type)).ToHashSet();
			if (acceptedMatchableTypes.Count == 0) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);
				return;
			}

			if (!await MatchActivelySemaphore.WaitAsync(0).ConfigureAwait(false)) {
				Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);
				return;
			}

			try {
				Bot.ArchiLogger.LogGenericTrace(Strings.Starting);

				bool match = true;

				for (byte i = 0; (i < MaxMatchingRounds) && match; i++) {
					if (i > 0) {
						// After each round we wait at least 5 minutes for all bots to react
						await Task.Delay(5 * 60 * 1000).ConfigureAwait(false);
					}

					if (!Bot.IsConnectedAndLoggedOn || Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) || !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively) || !await IsEligibleForMatching().ConfigureAwait(false)) {
						Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);
						break;
					}

					using (await Bot.Actions.GetTradingLock().ConfigureAwait(false)) {
						Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ActivelyMatchingItems, i));
						match = await MatchActivelyRound(acceptedMatchableTypes).ConfigureAwait(false);
						Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.DoneActivelyMatchingItems, i));
					}
				}

				Bot.ArchiLogger.LogGenericTrace(Strings.Done);
			} finally {
				MatchActivelySemaphore.Release();
			}
		}

		[SuppressMessage("ReSharper", "FunctionComplexityOverflow")]
		private async Task<bool> MatchActivelyRound(IReadOnlyCollection<Steam.Asset.EType> acceptedMatchableTypes) {
			if ((acceptedMatchableTypes == null) || (acceptedMatchableTypes.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(acceptedMatchableTypes));
				return false;
			}

			HashSet<Steam.Asset> ourInventory = await Bot.ArchiWebHandler.GetInventory(Bot.SteamID, tradable: true, wantedTypes: acceptedMatchableTypes).ConfigureAwait(false);
			if ((ourInventory == null) || (ourInventory.Count == 0)) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(ourInventory)));
				return false;
			}

			Dictionary<(uint AppID, Steam.Asset.EType Type), Dictionary<ulong, uint>> ourInventoryState = Trading.GetInventoryState(ourInventory);

			if (ourInventoryState.Values.All(set => set.Values.All(amount => amount <= 1))) {
				// User doesn't have any more dupes in the inventory
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(ourInventoryState)));
				return false;
			}

			HashSet<ListedUser> listedUsers = await GetListedUsers().ConfigureAwait(false);
			if ((listedUsers == null) || (listedUsers.Count == 0)) {
				Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(listedUsers)));
				return false;
			}

			byte emptyMatches = 0;
			HashSet<(uint AppID, Steam.Asset.EType Type)> skippedSetsThisRound = new HashSet<(uint AppID, Steam.Asset.EType Type)>();

			foreach (ListedUser listedUser in listedUsers.Where(listedUser => listedUser.MatchEverything && acceptedMatchableTypes.Any(listedUser.MatchableTypes.Contains) && !Bot.IsBlacklistedFromTrades(listedUser.SteamID)).OrderByDescending(listedUser => listedUser.Score).Take(MaxMatchedBotsHard)) {
				Bot.ArchiLogger.LogGenericTrace(listedUser.SteamID + "...");

				HashSet<Steam.Asset> theirInventory = await Bot.ArchiWebHandler.GetInventory(listedUser.SteamID, tradable: true, wantedSets: ourInventoryState.Keys, skippedSets: skippedSetsThisRound).ConfigureAwait(false);
				if ((theirInventory == null) || (theirInventory.Count == 0)) {
					Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(theirInventory)));
					continue;
				}

				HashSet<(uint AppID, Steam.Asset.EType Type)> skippedSetsThisUser = new HashSet<(uint AppID, Steam.Asset.EType Type)>();
				Dictionary<(uint AppID, Steam.Asset.EType Type), Dictionary<ulong, uint>> theirInventoryState = Trading.GetInventoryState(theirInventory);

				for (byte i = 0; i < Trading.MaxTradesPerAccount; i++) {
					byte itemsInTrade = 0;

					Dictionary<ulong, uint> classIDsToGive = new Dictionary<ulong, uint>();
					Dictionary<ulong, uint> classIDsToReceive = new Dictionary<ulong, uint>();

					foreach (KeyValuePair<(uint AppID, Steam.Asset.EType Type), Dictionary<ulong, uint>> ourInventoryStateSet in ourInventoryState.Where(set => listedUser.MatchableTypes.Contains(set.Key.Type) && set.Value.Values.Any(count => count > 1))) {
						if (!theirInventoryState.TryGetValue(ourInventoryStateSet.Key, out Dictionary<ulong, uint> theirItems)) {
							continue;
						}

						bool match;

						do {
							match = false;

							foreach (KeyValuePair<ulong, uint> ourItem in ourInventoryStateSet.Value.Where(item => item.Value > 1).OrderByDescending(item => item.Value)) {
								foreach (KeyValuePair<ulong, uint> theirItem in theirItems.OrderBy(item => ourInventoryStateSet.Value.TryGetValue(item.Key, out uint ourAmount) ? ourAmount : 0)) {
									if (ourInventoryStateSet.Value.TryGetValue(theirItem.Key, out uint ourAmountOfTheirItem) && (ourItem.Value <= ourAmountOfTheirItem + 1)) {
										continue;
									}

									// Skip this set from the remaining of this round
									skippedSetsThisUser.Add(ourInventoryStateSet.Key);

									// Update our state based on given items
									classIDsToGive[ourItem.Key] = classIDsToGive.TryGetValue(ourItem.Key, out uint givenAmount) ? givenAmount + 1 : 1;
									ourInventoryStateSet.Value[ourItem.Key] = ourItem.Value - 1;

									// Update our state based on received items
									classIDsToReceive[theirItem.Key] = classIDsToReceive.TryGetValue(theirItem.Key, out uint receivedAmount) ? receivedAmount + 1 : 1;
									ourInventoryStateSet.Value[theirItem.Key] = ourAmountOfTheirItem + 1;

									// Update their state based on taken items
									if (theirItems.TryGetValue(theirItem.Key, out uint theirAmount) && (theirAmount > 1)) {
										theirItems[theirItem.Key] = theirAmount - 1;
									} else {
										theirItems.Remove(theirItem.Key);
									}

									itemsInTrade += 2;

									match = true;
									break;
								}

								if (match) {
									break;
								}
							}
						} while (match && (itemsInTrade < Trading.MaxItemsPerTrade - 1));

						if (itemsInTrade >= Trading.MaxItemsPerTrade - 1) {
							break;
						}
					}

					if ((classIDsToGive.Count == 0) && (classIDsToReceive.Count == 0)) {
						Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(classIDsToGive)));

						if (++emptyMatches >= MaxMatchesBotsSoft) {
							Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ActivelyMatchingItemsRound, skippedSetsThisRound.Count));
							return skippedSetsThisRound.Count > 0;
						}

						break;
					}

					emptyMatches = 0;

					HashSet<Steam.Asset> itemsToGive = Trading.GetItemsFromInventory(ourInventory, classIDsToGive);
					HashSet<Steam.Asset> itemsToReceive = Trading.GetItemsFromInventory(theirInventory, classIDsToReceive);

					Bot.ArchiLogger.LogGenericTrace(Bot.SteamID + " <- " + string.Join(", ", itemsToReceive.Select(item => item.RealAppID + "/" + item.Type + "-" + item.ClassID + " #" + item.Amount)) + " | " + string.Join(", ", itemsToGive.Select(item => item.RealAppID + "/" + item.Type + "-" + item.ClassID + " #" + item.Amount)) + " -> " + listedUser.SteamID);

					(bool success, HashSet<ulong> mobileTradeOfferIDs) = await Bot.ArchiWebHandler.SendTradeOffer(listedUser.SteamID, itemsToGive, itemsToReceive, listedUser.TradeToken, true).ConfigureAwait(false);

					if ((mobileTradeOfferIDs != null) && (mobileTradeOfferIDs.Count > 0) && Bot.HasMobileAuthenticator) {
						if (!await Bot.Actions.AcceptConfirmations(true, Steam.ConfirmationDetails.EType.Trade, listedUser.SteamID, mobileTradeOfferIDs, true).ConfigureAwait(false)) {
							Bot.ArchiLogger.LogGenericTrace(Strings.WarningFailed);
							return false;
						}
					}

					if (!success) {
						Bot.ArchiLogger.LogGenericTrace(Strings.WarningFailed);
						continue;
					}

					Bot.ArchiLogger.LogGenericTrace(Strings.Success);
				}

				if (skippedSetsThisUser.Count == 0) {
					continue;
				}

				skippedSetsThisRound.UnionWith(skippedSetsThisUser);

				foreach ((uint AppID, Steam.Asset.EType Type) skippedSet in skippedSetsThisUser) {
					ourInventoryState.Remove(skippedSet);
				}

				if (ourInventoryState.Values.All(set => set.Values.All(amount => amount <= 1))) {
					// User doesn't have any more dupes in the inventory
					Bot.ArchiLogger.LogGenericTrace(string.Format(Strings.ErrorIsEmpty, nameof(ourInventoryState)));
					break;
				}
			}

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ActivelyMatchingItemsRound, skippedSetsThisRound.Count));
			return skippedSetsThisRound.Count > 0;
		}

		private sealed class ListedUser {
			internal readonly HashSet<Steam.Asset.EType> MatchableTypes = new HashSet<Steam.Asset.EType>();

#pragma warning disable 649
			[JsonProperty(PropertyName = "steam_id", Required = Required.Always)]
			internal readonly ulong SteamID;
#pragma warning restore 649

#pragma warning disable 649
			[JsonProperty(PropertyName = "trade_token", Required = Required.Always)]
			internal readonly string TradeToken;
#pragma warning restore 649

			internal float Score => GamesCount / (float) ItemsCount;

#pragma warning disable 649
			[JsonProperty(PropertyName = "games_count", Required = Required.Always)]
			private readonly ushort GamesCount;
#pragma warning restore 649

#pragma warning disable 649
			[JsonProperty(PropertyName = "items_count", Required = Required.Always)]
			private readonly ushort ItemsCount;
#pragma warning restore 649

			internal bool MatchEverything { get; private set; }

			[JsonProperty(PropertyName = "matchable_backgrounds", Required = Required.Always)]
			private byte MatchableBackgroundsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.ProfileBackground);
							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.ProfileBackground);
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));
							return;
					}
				}
			}

			[JsonProperty(PropertyName = "matchable_cards", Required = Required.Always)]
			private byte MatchableCardsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.TradingCard);
							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.TradingCard);
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));
							return;
					}
				}
			}

			[JsonProperty(PropertyName = "matchable_emoticons", Required = Required.Always)]
			private byte MatchableEmoticonsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.Emoticon);
							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.Emoticon);
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));
							return;
					}
				}
			}

			[JsonProperty(PropertyName = "matchable_foil_cards", Required = Required.Always)]
			private byte MatchableFoilCardsNumber {
				set {
					switch (value) {
						case 0:
							MatchableTypes.Remove(Steam.Asset.EType.FoilTradingCard);
							break;
						case 1:
							MatchableTypes.Add(Steam.Asset.EType.FoilTradingCard);
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));
							return;
					}
				}
			}

			[JsonProperty(PropertyName = "match_everything", Required = Required.Always)]
			private byte MatchEverythingNumber {
				set {
					switch (value) {
						case 0:
							MatchEverything = false;
							break;
						case 1:
							MatchEverything = true;
							break;
						default:
							ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(value), value));
							return;
					}
				}
			}
		}
	}
}
