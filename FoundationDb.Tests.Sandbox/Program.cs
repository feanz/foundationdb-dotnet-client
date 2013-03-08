﻿using System;
using FoundationDb.Client;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace FoundationDb.Tests.Sandbox
{
	class Program
	{

		static void ExecuteAsync(Func<Task> code)
		{
			// poor man's async main loop
			Task.Run(code).GetAwaiter().GetResult();
		}

		static void Main(string[] args)
		{
			try
			{
				ExecuteAsync(() => MainAsync(args));
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Oops! something went wrong:");
				Console.Error.WriteLine(e.ToString());
			}
			Console.WriteLine("[PRESS A KEY TO EXIT]");
			Console.ReadKey();
		}

		static async Task MainAsync(string[] args)
		{
			FdbCore.NativeLibPath = @"C:\Program Files\foundationdb\bin";
			FdbCore.TracePath = Path.Combine(Path.GetTempPath(), "fdb");

			int apiVersion = FdbCore.GetMaxApiVersion();
			Console.WriteLine("Max API Version: " + apiVersion);

			try
			{
				Console.WriteLine("Starting network thread...");
				FdbCore.Start(); // this will select API version 21			
				Console.WriteLine("> Up and running");

				Console.WriteLine("Connecting to local cluster...");
				using (var cluster = await FdbCore.ConnectAsync(null))
				{
					Console.WriteLine("> Connected!");

					Console.WriteLine("Opening database 'DB'...");
					using (var db = await cluster.OpenDatabaseAsync("DB"))
					{
						Console.WriteLine("> Connected to db '{0}'", db.Name);

						Console.WriteLine("Starting new transaction...");
						using (var trans = db.BeginTransaction())
						{
							Console.WriteLine("> Transaction ready");

							Console.WriteLine("Getting read version...");
							var readVersion = await trans.GetReadVersion();
							Console.WriteLine("> Read Version = " + readVersion);

							Console.WriteLine("Getting 'hello'...");
							var result = await trans.GetAsync("hello");
							//var result = trans.Get("hello");
							if (result == null)
								Console.WriteLine("> hello NOT FOUND");
							else
								Console.WriteLine("> hello = " + Encoding.UTF8.GetString(result));

							Console.WriteLine("Setting 'Foo' = 'Bar'");
							trans.Set("Foo", "Bar");

							Console.WriteLine("Setting 'TopSecret' = rnd(512)");
							var data = new byte[512];
							new Random(1234).NextBytes(data);
							trans.Set("TopSecret", data);

							Console.WriteLine("Committing transaction...");
							await trans.CommitAsync();
							//trans.Commit();
							Console.WriteLine("> Committed!");
						}

					const int N = 1000;

					#region INSERT...

					var insert = Stopwatch.StartNew();
					using (var trans = db.BeginTransaction())
					{
						for (int i = 0; i < N; i++)
						{
							trans.Set("hello" + i, "world" + i);
						}
						await trans.CommitAsync();
					}
					insert.Stop();
					Console.WriteLine("Took " + insert.Elapsed + " to insert " + N + " items (" + (insert.Elapsed.TotalMilliseconds / N) + "/write)");

					#endregion

					#region READ ASYNC

					var read = Stopwatch.StartNew();
					using (var trans = db.BeginTransaction())
					{
						for (int i = 0; i < N; i++)
						{
							var result = await trans.GetAsync("hello"+ i);
						}
						await trans.CommitAsync();
					}
					read.Stop();
					Console.WriteLine("Took " + read.Elapsed + " to read " + N + " items (" + (read.Elapsed.TotalMilliseconds / 1000) + "/read)");

					#endregion

					#region READ BLOCKING

					var read2 = Stopwatch.StartNew();
					using (var trans = db.BeginTransaction())
					{
						for (int i = 0; i < N; i++)
						{
							var result = trans.Get("hello" + i);
						}
						await trans.CommitAsync();
					}
					read2.Stop();
					Console.WriteLine("Took " + read2.Elapsed + " to read " + N + " items (" + (read2.Elapsed.TotalMilliseconds / 1000) + "/read)");

					#endregion

					#region CLEAR

					var clear = Stopwatch.StartNew();
					using (var trans = db.BeginTransaction())
					{
						for (int i = 0; i < N; i++)
						{
							trans.Clear("hello" + i);
						}

						await trans.CommitAsync();
					}
					clear.Stop();
					Console.WriteLine("Took " + clear.Elapsed + " to clear " + N + " items (" + (clear.Elapsed.TotalMilliseconds / 1000) + "/write)");

					#endregion

						Console.WriteLine("time to say goodbye...");
					}
				}
			}
			finally
			{
				Console.WriteLine("### DONE ###");
				FdbCore.Stop();
			}
		}
	}
}