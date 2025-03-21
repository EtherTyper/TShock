﻿using System;
using System.Collections.Generic;
using System.Linq;

using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.ObjectData;

using TShockAPI.Net;

namespace TShockAPI.Handlers
{
	/// <summary>
	/// Provides processors for handling Tile Rect packets
	/// </summary>
	public class SendTileRectHandler : IPacketHandler<GetDataHandlers.SendTileRectEventArgs>
	{
		/// <summary>
		/// Maps plant tile types to their valid grass ground tiles when using flower boots
		/// </summary>
		private static readonly Dictionary<ushort, HashSet<ushort>> FlowerBootPlantToGrassMap = new Dictionary<ushort, HashSet<ushort>>
		{
			{ TileID.Plants, new HashSet<ushort>()
			{
				TileID.Grass, TileID.GolfGrass
			} },
			{ TileID.HallowedPlants, new HashSet<ushort>()
			{
				TileID.HallowedGrass, TileID.GolfGrassHallowed
			} },
			{ TileID.HallowedPlants2, new HashSet<ushort>()
			{
				TileID.HallowedGrass, TileID.GolfGrassHallowed
			} },
			{ TileID.JunglePlants2, new HashSet<ushort>()
			{
				TileID.JungleGrass
			} },
		};

		/// <summary>
		/// Maps plant tile types to a list of valid styles, which are used to determine the FrameX value of the plant tile
		/// See `Player.DoBootsEffect_PlaceFlowersOnTile`
		/// </summary>
		private static readonly Dictionary<ushort, HashSet<ushort>> FlowerBootPlantToStyleMap = new Dictionary<ushort, HashSet<ushort>>()
		{
			{ TileID.Plants, new HashSet<ushort>()
			{
				// The upper line is from a `NextFromList` call
				// The lower line is from an additional switch which will add the listed options by adding a random value to a select set of styles
				6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 24, 27, 30, 33, 36, 39, 42,
				22, 23, 25, 26, 28, 29, 31, 32, 34, 35, 37, 38, 40, 41, 43, 44,
			} },
			{ TileID.HallowedPlants, new HashSet<ushort>()
			{
				// 5 is intentionally missing here because it is being skipped by vanilla
				4, 6,
			} },
			{ TileID.HallowedPlants2, new HashSet<ushort>()
			{
				// 5 is intentionally missing here because it is being skipped by vanilla
				2, 3, 4, 6, 7,
			} },
			{ TileID.JunglePlants2, new HashSet<ushort>()
			{
				9, 10, 11, 12, 13, 14, 15, 16,
			} },
		};

		/// <summary>
		/// Item IDs that can spawn flowers while you walk
		/// </summary>
		public static List<int> FlowerBootItems = new List<int>
		{
			ItemID.FlowerBoots,
			ItemID.FairyBoots
		};

		/// <summary>
		/// Maps TileIDs to Tile Entity IDs.
		/// Note: <see cref="TileEntityID"/> is empty at the time of writing, but entities are dynamically assigned their ID at initialize time
		/// which is why we can use the _myEntityId field on each entity type
		/// </summary>
		public static Dictionary<int, int> TileEntityIdToTileIdMap = new Dictionary<int, int>
		{
			{ TileID.TargetDummy,        TETrainingDummy._myEntityID },
			{ TileID.ItemFrame,          TEItemFrame._myEntityID },
			{ TileID.LogicSensor,        TELogicSensor._myEntityID },
			{ TileID.DisplayDoll,        TEDisplayDoll._myEntityID },
			{ TileID.WeaponsRack2,       TEWeaponsRack._myEntityID },
			{ TileID.HatRack,            TEHatRack._myEntityID },
			{ TileID.FoodPlatter,        TEFoodPlatter._myEntityID },
			{ TileID.TeleportationPylon, TETeleportationPylon._myEntityID }
		};

		/// <summary>
		/// Invoked when a SendTileRect packet is received
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="args"></param>
		public void OnReceive(object sender, GetDataHandlers.SendTileRectEventArgs args)
		{
			// By default, we'll handle everything
			args.Handled = true;

			if (ShouldSkipProcessing(args))
			{
				return;
			}

			bool[,] processed = new bool[args.Width, args.Length];
			NetTile[,] tiles = ReadNetTilesFromStream(args.Data, args.Width, args.Length);

			Debug.VisualiseTileSetDiff(args.TileX, args.TileY, args.Width, args.Length, tiles);

			IterateTileRect(tiles, processed, args);

			// Uncommenting this function will send the same tile rect 10 blocks above you for visualisation. This will modify your world and overwrite existing blocks.
			// Use in test worlds only.
			//Debug.DisplayTileSetInGame(args.TileX, (short)(args.TileY - 10), args.Width, args.Length, tiles, args.Player);

			// If we are handling this event then we have updated the server's Main.tile state the way we want it.
			// At this point we should send our state back to the client so they remain in sync with the server
			if (args.Handled == true)
			{
				TSPlayer.All.SendTileRect(args.TileX, args.TileY, args.Width, args.Length);
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect reimplemented from carbonara from {0}", args.Player.Name);
			}
		}

		/// <summary>
		/// Iterates over each tile in the tile rectangle and performs processing on individual tiles or multi-tile Tile Objects
		/// </summary>
		/// <param name="tiles"></param>
		/// <param name="processed"></param>
		/// <param name="args"></param>
		internal void IterateTileRect(NetTile[,] tiles, bool[,] processed, GetDataHandlers.SendTileRectEventArgs args)
		{
			int tileX = args.TileX;
			int tileY = args.TileY;
			byte width = args.Width;
			byte length = args.Length;

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < length; y++)
				{
					// Do not process already processed tiles
					if (processed[x, y])
					{
						continue;
					}

					int realX = tileX + x;
					int realY = tileY + y;

					// Do not process tiles outside of the world boundaries
					if ((realX < 0 || realX >= Main.maxTilesX)
						|| (realY < 0 || realY > Main.maxTilesY))
					{
						processed[x, y] = true;
						continue;
					}

					// Do not process tiles that the player cannot update
					if (!args.Player.HasBuildPermission(realX, realY) ||
						!args.Player.IsInRange(realX, realY))
					{
						processed[x, y] = true;
						continue;
					}

					NetTile newTile = tiles[x, y];
					TileObjectData data;

					// If the new tile has an associated TileObjectData object, we take the tile and the surrounding tiles that make up the tile object
					// and process them as a tile object
					if (newTile.Type < TileObjectData._data.Count && TileObjectData._data[newTile.Type] != null)
					{
						// Verify that the changes are actually valid conceptually
						// Many tiles that are never placed or modified using this packet are valid TileObjectData entries, which is the main attack vector for most exploits using this packet
						if (Main.tile[realX, realY].type == newTile.Type)
						{
							switch (newTile.Type)
							{
								// Some individual cases might still allow crashing exploits, as the actual framing is not being checked here
								// Doing so requires hard-coding the individual valid framing values and is a lot of effort
								case TileID.ProjectilePressurePad:
								case TileID.WirePipe:
								case TileID.Traps:
								case TileID.Candles:
								case TileID.PeaceCandle:
								case TileID.WaterCandle:
								case TileID.PlatinumCandle:
								case TileID.Firework:
								case TileID.WaterFountain:
								case TileID.BloodMoonMonolith:
								case TileID.VoidMonolith:
								case TileID.LunarMonolith:
								case TileID.MusicBoxes:
								case TileID.ArrowSign:
								case TileID.PaintedArrowSign:
								case TileID.Cannon:
								case TileID.Campfire:
								case TileID.Plants:
								case TileID.MinecartTrack:
								case TileID.ChristmasTree:
									{
										// Allowed changes
									}
									break;
								default:
									{
										continue;
									}
							}
						}
						else
						{
							// Together with Flower Boots and Land Mine destruction, these are the only cases where a tile type is allowed to be modified
							switch (newTile.Type)
							{
								case TileID.LogicSensor:
								case TileID.FoodPlatter:
								case TileID.WeaponsRack2:
								case TileID.ItemFrame:
								case TileID.HatRack:
								case TileID.DisplayDoll:
								case TileID.TeleportationPylon:
								case TileID.TargetDummy:
									{
										// Allowed placements
									}
									break;
								default:
									{
										continue;
									}
							}
						}

						data = TileObjectData._data[newTile.Type];
						NetTile[,] newTiles;
						int objWidth = data.Width;
						int objHeight = data.Height;

						// Ensure the tile object fits inside the rect before processing it
						if (!DoesTileObjectFitInTileRect(x, y, objWidth, objHeight, width, length, processed))
						{
							continue;
						}

						newTiles = new NetTile[objWidth, objHeight];

						for (int i = 0; i < objWidth; i++)
						{
							for (int j = 0; j < objHeight; j++)
							{
								newTiles[i, j] = tiles[x + i, y + j];
								processed[x + i, y + j] = true;
							}
						}
						ProcessTileObject(newTile.Type, realX, realY, objWidth, objHeight, newTiles, args);
						continue;
					}

					// If the new tile does not have an associated tile object, process it as an individual tile
					ProcessSingleTile(realX, realY, newTile, width, length, args);
					processed[x, y] = true;
				}
			}
		}

		/// <summary>
		/// Processes a tile object consisting of multiple tiles from the tile rect packet
		/// </summary>
		/// <param name="tileType">The tile type the object is comprised of</param>
		/// <param name="newTiles">2D array of NetTile containing the new tiles properties</param>
		/// <param name="realX">X position at the top left of the object</param>
		/// <param name="realY">Y position at the top left of the object</param>
		/// <param name="width">Width of the tile object</param>
		/// <param name="height">Height of the tile object</param>
		/// <param name="args">SendTileRectEventArgs containing event information</param>
		internal void ProcessTileObject(int tileType, int realX, int realY, int width, int height, NetTile[,] newTiles, GetDataHandlers.SendTileRectEventArgs args)
		{
			// As long as the player has permission to build, we should allow a tile object to be placed
			// More in depth checks should take place in handlers for the Place Object (79), Update Tile Entity (86), and Place Tile Entity (87) packets
			if (!args.Player.HasBuildPermissionForTileObject(realX, realY, width, height))
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect rejected from no permission for tile object from {0}", args.Player.Name);
				return;
			}

			if (TShock.TileBans.TileIsBanned((short)tileType))
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect rejected for banned tile");
				return;
			}

			// Update all tiles in the tile object. These will be sent back to the player later
			UpdateMultipleServerTileStates(realX, realY, width, height, newTiles);

			// Tile entities have special placements that we should let the game deal with
			if (TileEntityIdToTileIdMap.ContainsKey(tileType))
			{
				TileEntity.PlaceEntityNet(realX, realY, TileEntityIdToTileIdMap[tileType]);
			}
		}

		/// <summary>
		/// Processes a single tile from the tile rect packet
		/// </summary>
		/// <param name="realX">X position at the top left of the object</param>
		/// <param name="realY">Y position at the top left of the object</param>
		/// <param name="newTile">The NetTile containing new tile properties</param>
		/// <param name="rectWidth">The width of the rectangle being processed</param>
		/// <param name="rectLength">The length of the rectangle being processed</param>
		/// <param name="args">SendTileRectEventArgs containing event information</param>
		internal void ProcessSingleTile(int realX, int realY, NetTile newTile, byte rectWidth, byte rectLength, GetDataHandlers.SendTileRectEventArgs args)
		{
			// Some boots allow growing flowers on grass. This process sends a 1x1 tile rect to grow the flowers
			// The rect size must be 1 and the player must have an accessory that allows growing flowers in order for this rect to be valid
			if (rectWidth == 1 && rectLength == 1 && WorldGen.InWorld(realX, realY + 1) && args.Player.Accessories.Any(a => a != null && FlowerBootItems.Contains(a.type)))
			{
				ProcessFlowerBoots(realX, realY, newTile);
				return;
			}

			ITile tile = Main.tile[realX, realY];

			// Triggering a single land mine tile
			if (rectWidth == 1 && rectLength == 1 && tile.type == TileID.LandMine && !newTile.Active)
			{
				UpdateServerTileState(tile, newTile, TileDataType.Tile);
			}

			// Hammering a single junction box
			if (rectWidth == 1 && rectLength == 1 && tile.type == TileID.WirePipe)
			{
				UpdateServerTileState(tile, newTile, TileDataType.Tile);
			}

			// Mowing a single grass tile: Grass -> GolfGrass OR HallowedGrass -> GolfGrassHallowed
			if (rectWidth == 1 && rectLength == 1 &&
				(
					tile.type == TileID.Grass && newTile.Type == TileID.GolfGrass ||
					tile.type == TileID.HallowedGrass && newTile.Type == TileID.GolfGrassHallowed
				))
			{
				UpdateServerTileState(tile, newTile, TileDataType.Tile);
			}

			// Conversion: only sends a 1x1 rect
			if (rectWidth == 1 && rectLength == 1)
			{
				ProcessConversionSpreads(tile, newTile);
			}

			// All other single tile updates should not be processed.
		}

		/// <summary>
		/// Applies changes to a tile if a tile rect for flower-growing boots is valid
		/// </summary>
		/// <param name="realX">The tile x position of the tile rect packet - this is where the flowers are intending to grow</param>
		/// <param name="realY">The tile y position of the tile rect packet - this is where the flowers are intending to grow</param>
		/// <param name="newTile">The NetTile containing information about the flowers that are being grown</param>
		internal void ProcessFlowerBoots(int realX, int realY, NetTile newTile)
		{
			ITile tile = Main.tile[realX, realY];
			// Ensure that:
			//  - the placed plant is valid for the grass below
			//  - the target tile is empty
			//  - and the placed plant has valid framing (style * 18 = FrameX)
			if (
				FlowerBootPlantToGrassMap.TryGetValue(newTile.Type, out HashSet<ushort> grassTiles) &&
				!tile.active() &&
				grassTiles.Contains(Main.tile[realX, realY + 1].type) &&
				FlowerBootPlantToStyleMap[newTile.Type].Contains((ushort)(newTile.FrameX / 18))
			)
			{
				UpdateServerTileState(tile, newTile, TileDataType.Tile);
			}
		}

		/// <summary>
		/// Updates a single tile on the server if it is a valid conversion from one tile or wall type to another (eg stone -> corrupt stone)
		/// </summary>
		/// <param name="tile">The tile to update</param>
		/// <param name="newTile">The NetTile containing new tile properties</param>
		internal void ProcessConversionSpreads(ITile tile, NetTile newTile)
		{
			// Update if the existing tile or wall is convertible and the new tile or wall is a valid conversion
			if (
				((TileID.Sets.Conversion.Stone[tile.type] || Main.tileMoss[tile.type]) && (TileID.Sets.Conversion.Stone[newTile.Type] || Main.tileMoss[newTile.Type])) ||
				((tile.type == 0 || tile.type == 59) && (newTile.Type == 0 || newTile.Type == 59)) ||
				TileID.Sets.Conversion.Grass[tile.type] && TileID.Sets.Conversion.Grass[newTile.Type] ||
				TileID.Sets.Conversion.Ice[tile.type] && TileID.Sets.Conversion.Ice[newTile.Type] ||
				TileID.Sets.Conversion.Sand[tile.type] && TileID.Sets.Conversion.Sand[newTile.Type] ||
				TileID.Sets.Conversion.Sandstone[tile.type] && TileID.Sets.Conversion.Sandstone[newTile.Type] ||
				TileID.Sets.Conversion.HardenedSand[tile.type] && TileID.Sets.Conversion.HardenedSand[newTile.Type] ||
				TileID.Sets.Conversion.Thorn[tile.type] && TileID.Sets.Conversion.Thorn[newTile.Type] ||
				TileID.Sets.Conversion.Moss[tile.type] && TileID.Sets.Conversion.Moss[newTile.Type] ||
				TileID.Sets.Conversion.MossBrick[tile.type] && TileID.Sets.Conversion.MossBrick[newTile.Type]
			)
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect processing a tile conversion update - [{0}] -> [{1}]", tile.type, newTile.Type);
				UpdateServerTileState(tile, newTile, TileDataType.Tile);
			}

			if (WallID.Sets.Conversion.Stone[tile.wall] && WallID.Sets.Conversion.Stone[newTile.Wall] ||
				WallID.Sets.Conversion.Grass[tile.wall] && WallID.Sets.Conversion.Grass[newTile.Wall] ||
				WallID.Sets.Conversion.Sandstone[tile.wall] && WallID.Sets.Conversion.Sandstone[newTile.Wall] ||
				WallID.Sets.Conversion.HardenedSand[tile.wall] && WallID.Sets.Conversion.HardenedSand[newTile.Wall] ||
				WallID.Sets.Conversion.PureSand[tile.wall] && WallID.Sets.Conversion.PureSand[newTile.Wall] ||
				WallID.Sets.Conversion.NewWall1[tile.wall] && WallID.Sets.Conversion.NewWall1[newTile.Wall] ||
				WallID.Sets.Conversion.NewWall2[tile.wall] && WallID.Sets.Conversion.NewWall2[newTile.Wall] ||
				WallID.Sets.Conversion.NewWall3[tile.wall] && WallID.Sets.Conversion.NewWall3[newTile.Wall] ||
				WallID.Sets.Conversion.NewWall4[tile.wall] && WallID.Sets.Conversion.NewWall4[newTile.Wall]
			)
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect processing a wall conversion update - [{0}] -> [{1}]", tile.wall, newTile.Wall);
				UpdateServerTileState(tile, newTile, TileDataType.Wall);
			}
		}

		/// <summary>
		/// Updates a single tile's world state with a set of changes from the networked tile state
		/// </summary>
		/// <param name="tile">The tile to update</param>
		/// <param name="newTile">The NetTile containing the change</param>
		/// <param name="updateType">The type of data to merge into world state</param>
		public static void UpdateServerTileState(ITile tile, NetTile newTile, TileDataType updateType)
		{
			//This logic (updateType & TDT.Tile) != 0 is the way Terraria does it (see: Tile.cs/Clear(TileDataType))
			//& is not a typo - we're performing a binary AND test to see if a given flag is set.

			if ((updateType & TileDataType.Tile) != 0)
			{
				tile.active(newTile.Active);
				tile.type = newTile.Type;

				if (newTile.FrameImportant)
				{
					tile.frameX = newTile.FrameX;
					tile.frameY = newTile.FrameY;
				}
				else if (tile.type != newTile.Type || !tile.active())
				{
					//This is vanilla logic - if the tile changed types (or wasn't active) the frame values might not be valid - so we reset them to -1.
					tile.frameX = -1;
					tile.frameY = -1;
				}
			}

			if ((updateType & TileDataType.Wall) != 0)
			{
				tile.wall = newTile.Wall;
			}

			if ((updateType & TileDataType.TilePaint) != 0)
			{
				tile.color(newTile.TileColor);
			}

			if ((updateType & TileDataType.WallPaint) != 0)
			{
				tile.wallColor(newTile.WallColor);
			}

			if ((updateType & TileDataType.Liquid) != 0)
			{
				tile.liquid = newTile.Liquid;
				tile.liquidType(newTile.LiquidType);
			}

			if ((updateType & TileDataType.Slope) != 0)
			{
				tile.halfBrick(newTile.IsHalf);
				tile.slope(newTile.Slope);
			}

			if ((updateType & TileDataType.Wiring) != 0)
			{
				tile.wire(newTile.Wire);
				tile.wire2(newTile.Wire2);
				tile.wire3(newTile.Wire3);
				tile.wire4(newTile.Wire4);
			}

			if ((updateType & TileDataType.Actuator) != 0)
			{
				tile.actuator(newTile.IsActuator);
				tile.inActive(newTile.Inactive);
			}
		}

		/// <summary>
		/// Performs <see cref="UpdateServerTileState(ITile, NetTile, TileDataType)"/> on multiple tiles
		/// </summary>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <param name="width"></param>
		/// <param name="height"></param>
		/// <param name="newTiles"></param>
		public static void UpdateMultipleServerTileStates(int x, int y, int width, int height, NetTile[,] newTiles)
		{
			for (int i = 0; i < width; i++)
			{
				for (int j = 0; j < height; j++)
				{
					UpdateServerTileState(Main.tile[x + i, y + j], newTiles[i, j], TileDataType.Tile);
				}
			}
		}

		/// <summary>
		/// Reads a set of NetTiles from a memory stream
		/// </summary>
		/// <param name="stream"></param>
		/// <param name="width"></param>
		/// <param name="length"></param>
		/// <returns></returns>
		static NetTile[,] ReadNetTilesFromStream(System.IO.MemoryStream stream, byte width, byte length)
		{
			NetTile[,] tiles = new NetTile[width, length];
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < length; y++)
				{
					tiles[x, y] = new NetTile(stream);
				}
			}

			return tiles;
		}

		/// <summary>
		/// Determines whether or not the tile rect should be immediately accepted or rejected
		/// </summary>
		/// <param name="args"></param>
		/// <returns></returns>
		static bool ShouldSkipProcessing(GetDataHandlers.SendTileRectEventArgs args)
		{
			if (args.Player.HasPermission(Permissions.allowclientsideworldedit))
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect accepted clientside world edit from {0}", args.Player.Name);
				args.Handled = false;
				return true;
			}

			if (args.Width > 4 || args.Length > 4) // as of 1.4.3.6 this is the biggest size the client will send in any case
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect rejected from non-vanilla tilemod from {0}", args.Player.Name);
				return true;
			}

			if (args.Player.IsBouncerThrottled())
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect rejected from throttle from {0}", args.Player.Name);
				args.Player.SendTileRect(args.TileX, args.TileY, args.Length, args.Width);
				return true;
			}

			if (args.Player.IsBeingDisabled())
			{
				TShock.Log.ConsoleDebug("Bouncer / SendTileRect rejected from being disabled from {0}", args.Player.Name);
				args.Player.SendTileRect(args.TileX, args.TileY, args.Length, args.Width);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Checks if a tile object fits inside the dimensions of a tile rectangle
		/// </summary>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <param name="width"></param>
		/// <param name="height"></param>
		/// <param name="rectWidth"></param>
		/// <param name="rectLength"></param>
		/// <param name="processed"></param>
		/// <returns></returns>
		static bool DoesTileObjectFitInTileRect(int x, int y, int width, int height, short rectWidth, short rectLength, bool[,] processed)
		{
			if (x + width > rectWidth || y + height > rectLength)
			{
				// This is ugly, but we want to mark all these tiles as processed so that we're not hitting this check multiple times for one dodgy tile object
				for (int i = x; i < rectWidth; i++)
				{
					for (int j = y; j < rectLength; j++)
					{
						processed[i, j] = true;
					}
				}

				TShock.Log.ConsoleDebug("Bouncer / SendTileRectHandler - rejected tile object because object dimensions fall outside the tile rect (excessive size)");
				return false;
			}

			return true;
		}

		class Debug
		{
			/// <summary>
			/// Displays the difference in IDs between existing tiles and a set of NetTiles to the console
			/// </summary>
			/// <param name="tileX">X position at the top left of the rect</param>
			/// <param name="tileY">Y position at the top left of the rect</param>
			/// <param name="width">Width of the NetTile set</param>
			/// <param name="height">Height of the NetTile set</param>
			/// <param name="newTiles">New tiles to be visualised</param>
			public static void VisualiseTileSetDiff(int tileX, int tileY, int width, int height, NetTile[,] newTiles)
			{
				if (TShock.Config.Settings.DebugLogs)
				{
					char pad = '0';
					for (int y = 0; y < height; y++)
					{
						int realY = y + tileY;
						for (int x = 0; x < width; x++)
						{
							int realX = x + tileX;
							ushort type = Main.tile[realX, realY].type;
							string type2 = type.ToString();
							Console.Write((type2.ToString()).PadLeft(3, pad) + (Main.tile[realX, realY].active() ? "a" : "-") + " ");
						}
						Console.Write(" -> ");
						for (int x = 0; x < width; x++)
						{
							int realX = x + tileX;
							ushort type = newTiles[x, y].Type;
							string type2 = type.ToString();
							Console.Write((type2.ToString()).PadLeft(3, pad) + (newTiles[x, y].Active ? "a" : "-") + " ");
						}
						Console.Write("\n");
					}
				}
			}

			/// <summary>
			/// Sends a tile rect at the given (tileX, tileY) coordinate, using the given set of NetTiles information to update the tile rect
			/// </summary>
			/// <param name="tileX">X position at the top left of the rect</param>
			/// <param name="tileY">Y position at the top left of the rect</param>
			/// <param name="width">Width of the NetTile set</param>
			/// <param name="height">Height of the NetTile set</param>
			/// <param name="newTiles">New tiles to place in the rect</param>
			/// <param name="player">Player to send the debug display to</param>
			public static void DisplayTileSetInGame(short tileX, short tileY, byte width, byte height, NetTile[,] newTiles, TSPlayer player)
			{
				for (int x = 0; x < width; x++)
				{
					for (int y = 0; y < height; y++)
					{
						UpdateServerTileState(Main.tile[tileX + x, tileY + y], newTiles[x, y], TileDataType.All);
					}
					//Add a line of dirt blocks at the bottom for safety
					UpdateServerTileState(Main.tile[tileX + x, tileY + height], new NetTile { Active = true, Type = 0 }, TileDataType.All);
				}

				player.SendTileRect(tileX, tileY, width, height);
			}
		}
	}
}
