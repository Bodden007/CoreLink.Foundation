using CoreLink.Client.TestHost;

Console.WriteLine("CoreLink.Foundation TestHost");
Console.WriteLine();

await ModbusTransportSmokeTest.RunAsync();