var builder = DistributedApplication.CreateBuilder(args);

builder.AddRepoQL();

builder.AddProject<Projects.RepoQL_Aspire_Sample_ApiService>("apiservice");

builder.Build().Run();
