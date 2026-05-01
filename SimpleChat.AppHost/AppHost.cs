var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.SimpleChat>("simplechat");

builder.Build().Run();
