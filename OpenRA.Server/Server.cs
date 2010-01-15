﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using OpenRa.FileFormats;

namespace OpenRA.Server
{
	static class Server
	{
		static List<Connection> conns = new List<Connection>();
		static TcpListener listener = new TcpListener(IPAddress.Any, 1234);
		static Dictionary<int, List<Connection>> inFlightFrames
			= new Dictionary<int, List<Connection>>();
		static Session lobbyInfo = new Session();

		public static void Main(string[] args)
		{
			listener.Start();

			Console.WriteLine("Server started.");
			Console.WriteLine("Testing.");

			for (; ; )
			{
				var checkRead = new ArrayList();
				checkRead.Add(listener.Server);
				foreach (var c in conns) checkRead.Add(c.socket);

				/* msdn lies, -1 doesnt work. this is ~1h instead. */
				Socket.Select(checkRead, null, null, -2	);

				foreach (Socket s in checkRead)
					if (s == listener.Server) AcceptConnection();
					else ReadData(conns.Single(c => c.socket == s));
			}
		}

		static int ChooseFreePlayerIndex()
		{
			for (var i = 0; i < 8; i++)
				if (conns.All(c => c.PlayerIndex != i))
					return i;

			throw new InvalidOperationException("Already got 8 players");
		}

		static void AcceptConnection()
		{
			var newConn = new Connection { socket = listener.AcceptSocket() };
			try
			{
				newConn.socket.Blocking = false;
				newConn.socket.NoDelay = true;

				// assign the player number.
				newConn.PlayerIndex = ChooseFreePlayerIndex();

				conns.Add(newConn);

				lobbyInfo.Clients.Add(
					new Session.Client()
					{
						Index = newConn.PlayerIndex,
						Palette = newConn.PlayerIndex,
						Name = "Player {0}".F(1 + newConn.PlayerIndex),
						Race = 1,
						State = Session.ClientState.NotReady
					});

				DispatchOrdersToClient(newConn, 0,
					new ServerOrder(newConn.PlayerIndex, "AssignPlayer", "").Serialize());

				Console.WriteLine("Accepted connection from {0}.",
					newConn.socket.RemoteEndPoint);

				DispatchOrders(newConn, 0, new ServerOrder(newConn.PlayerIndex,
					"Chat", "has joined the game.").Serialize());

				SyncLobbyInfo();
			}
			catch (Exception e) { DropClient(newConn, e); }
		}

		static bool ReadDataInner(Connection conn)
		{
			var rx = new byte[1024];
			var len = 0;

			for (; ; )
			{
				try
				{
					if (0 < (len = conn.socket.Receive(rx)))
						conn.data.AddRange(rx.Take(len));
					else
						break;
				}
				catch (SocketException e)
				{
					if (e.SocketErrorCode == SocketError.WouldBlock) break;
					DropClient(conn, e); 
					return false; 
				}
			}

			return true;
		}

		static void ReadData(Connection conn)
		{
			if (ReadDataInner(conn))
				while (conn.data.Count >= conn.ExpectLength)
				{
					var bytes = conn.PopBytes(conn.ExpectLength);
					switch (conn.State)
					{
						case ReceiveState.Header:
							{
								conn.ExpectLength = BitConverter.ToInt32(bytes, 0) - 4;
								conn.Frame = BitConverter.ToInt32(bytes, 4);
								conn.State = ReceiveState.Data;
							} break;

						case ReceiveState.Data:
							{
						//		if (bytes.Length > 0)
						//			Console.WriteLine("{0} bytes", bytes.Length);

								DispatchOrders(conn, conn.Frame, bytes);
								conn.ExpectLength = 8;
								conn.State = ReceiveState.Header;

								UpdateInFlightFrames(conn);
							} break;
					}
				}
		}

		static void UpdateInFlightFrames(Connection conn)
		{
			if (conn.Frame != 0)
			{
				if (!inFlightFrames.ContainsKey(conn.Frame))
					inFlightFrames[conn.Frame] = new List<Connection> { conn };
				else
					inFlightFrames[conn.Frame].Add(conn);

				if (conns.All(c => inFlightFrames[conn.Frame].Contains(c)))
				{
					inFlightFrames.Remove(conn.Frame);
					DispatchOrders(null, conn.Frame, new byte[] { 0xef });
				}
			}
		}

		static void DispatchOrdersToClient(Connection c, int frame, byte[] data)
		{
			try
			{
				c.socket.Blocking = true;
				c.socket.Send(BitConverter.GetBytes(data.Length + 4));
				c.socket.Send(BitConverter.GetBytes(frame));
				c.socket.Send(data);
				c.socket.Blocking = false;
			}
			catch (Exception e) { DropClient(c, e); }
		}

		static void DispatchOrders(Connection conn, int frame, byte[] data)
		{
			if (frame == 0 && conn != null)
				InterpretServerOrders(conn, data);
			else
				foreach (var c in conns.Except(conn).ToArray())
					DispatchOrdersToClient(c, frame, data);
		}

		static void InterpretServerOrders(Connection conn, byte[] data)
		{
			var ms = new MemoryStream(data);
			var br = new BinaryReader(ms);

			try
			{
				for (; ; )
				{
					var so = ServerOrder.Deserialize(br);
					if (so == null) return;
					InterpretServerOrder(conn, so);
				}
			}
			catch (EndOfStreamException) { }
		}

		static bool InterpretCommand(Connection conn, string cmd)
		{
			var dict = new Dictionary<string, Func<string, bool>>
			{
				{ "name", 
					s => 
					{
						Console.WriteLine("Player@{0} is now known as {1}", conn.socket.RemoteEndPoint, s);
						lobbyInfo.Clients[ conn.PlayerIndex ].Name = s;
						SyncLobbyInfo();
						return true;
					}},
				{ "lag",
					s =>
					{
						int lag;
						if (!int.TryParse(s, out lag)) { Console.WriteLine("Invalid order lag: {0}", s); return false; }

						Console.WriteLine("Order lag is now {0} frames.", lag);

						lobbyInfo.GlobalSettings.OrderLatency = lag;
						SyncLobbyInfo();
						return true;
					}},
				{ "race",
					s => 
					{
						if (conn.IsReady) 
						{
							DispatchOrdersToClient(conn, 0, 
								new ServerOrder( conn.PlayerIndex, "Chat", 
									"You can't change your race after the game has started" ).Serialize() );
							return true;
						}

						int race;
						if (!int.TryParse(s, out race) || race < 0 || race > 1)
						{
							Console.WriteLine("Invalid race: {0}", s);
							return false;
						}

						lobbyInfo.Clients[ conn.PlayerIndex ].Race = 1 + race;
						SyncLobbyInfo();
						return true;
					}},
				{ "pal",
					s =>
					{
						if (conn.IsReady) 
						{
							DispatchOrdersToClient(conn, 0, 
								new ServerOrder( conn.PlayerIndex, "Chat", 
									"You can't change your color after the game has started" ).Serialize() );
							return true;
						}

						int pal;
						if (!int.TryParse(s, out pal) || pal < 0 || pal > 7)
						{
							Console.WriteLine("Invalid palette: {0}", s);
							return false;
						}

						lobbyInfo.Clients[ conn.PlayerIndex ].Palette = pal;
						SyncLobbyInfo();
						return true;
					}},
				{ "map",
					s =>
					{
						if (conn.PlayerIndex != 0)
						{
							DispatchOrdersToClient(conn, 0,
								new ServerOrder( conn.PlayerIndex, "Chat",
									"Only the host can change the map" ).Serialize() );
							return true;
						}

						if (conn.IsReady)
						{
							DispatchOrdersToClient(conn, 0, 
								new ServerOrder( conn.PlayerIndex, "Chat",
									"You can't change the map after the game has started" ).Serialize() );
							return true;
						}

						lobbyInfo.GlobalSettings.Map = s;
						SyncLobbyInfo();
						return true;
					}},
			};

			var cmdName = cmd.Split(' ').First();
			var cmdValue = string.Join(" ", cmd.Split(' ').Skip(1).ToArray());

			Func<string,bool> a;
			if (!dict.TryGetValue(cmdName, out a))
				return false;

			return a(cmdValue);
		}

		static void SendChat(Connection asConn, string text)
		{
			DispatchOrders(null, 0, new ServerOrder(asConn.PlayerIndex, "Chat", text).Serialize());
		}

		static void InterpretServerOrder(Connection conn, ServerOrder so)
		{
			switch (so.Name)
			{
				case "ToggleReady":
					conn.IsReady ^= true;

					Console.WriteLine("Player @{0} is {1}", 
						conn.socket.RemoteEndPoint, conn.IsReady ? "ready" : "not ready");

					// start the game if everyone is ready.
					if (conns.All(c => c.IsReady))
					{
						Console.WriteLine("All players are ready. Starting the game!");
						DispatchOrders(null, 0,
							new ServerOrder(0, "StartGame", "").Serialize());
					}
					break;

				case "Chat":
					if (so.Data.StartsWith("/"))
					{
						if (!InterpretCommand(conn, so.Data.Substring(1)))
						{
							Console.WriteLine("Bad server command: {0}", so.Data.Substring(1));
							DispatchOrdersToClient(conn, 0, new ServerOrder(conn.PlayerIndex, "Chat", "Bad server command.").Serialize());
						}
					}
					else
						foreach (var c in conns.Except(conn).ToArray())
							DispatchOrdersToClient(c, 0, so.Serialize());
					break;
			}
		}

		static void DropClient(Connection toDrop, Exception e)
		{
			Console.WriteLine("Client dropped: {0}.", toDrop.socket.RemoteEndPoint);
			//Console.WriteLine(e.ToString());

			conns.Remove(toDrop);
			SendChat(toDrop, "Connection Dropped");

			lobbyInfo.Clients.RemoveAll(c => c.Index == toDrop.PlayerIndex);

			/* don't get stuck waiting for the dropped player, if they were the one holding up a frame */
			
			foreach( var f in inFlightFrames.ToArray() )
			if (conns.All(c => f.Value.Contains(c)))
			{
				inFlightFrames.Remove(f.Key);
				DispatchOrders(null, f.Key, new byte[] { 0xef });
			}

			if (conns.Count == 0) OnServerEmpty();
			else SyncLobbyInfo();
		}

		public static void Write(this Stream s, byte[] data) { s.Write(data, 0, data.Length); }
		public static byte[] Read(this Stream s, int len) { var data = new byte[len]; s.Read(data, 0, len); return data; }
		public static IEnumerable<T> Except<T>(this IEnumerable<T> ts, T t)
		{
			return ts.Except(new[] { t });
		}

		static void OnServerEmpty()
		{
			Console.WriteLine("Server emptied out; doing a bit of housekeeping to prepare for next game..");
			inFlightFrames.Clear();
			lobbyInfo = new Session();
		}

		static void SyncLobbyInfo()
		{
			var clientData = lobbyInfo.Clients.ToDictionary(
				a => a.Index.ToString(),
				a => FieldSaver.Save(a));

			clientData["GlobalSettings"] = FieldSaver.Save(lobbyInfo.GlobalSettings);

			DispatchOrders(null, 0,
				new ServerOrder(0, "SyncInfo", clientData.WriteToString()).Serialize());
		}
	}
}
