using CleanArc.Application.Services.Cache;
using CleanArc.Domain.Entities.BaseInfos.Enums;
using CleanArc.Domain.Entities.Deposits;
using CleanArc.Domain.Entities.Deposits.Enums;
using CleanArc.Domain.Entities.Services.Enums;
using CleanArc.Infrastructure.Persistence;
using CleanArc.Web.RedisCache.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CleanArc.Infrastructure.BackgroundServices.DepositServices
{
    public class SystemArchiveBackgroundService(IServiceScopeFactory scopeFactory, IRedisCacheService redisCache) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var dbcontext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var rentDeposits = await dbcontext.Deposits.Include(ds => ds.DepositServices)
                        .Where(dci => dci.DepositCategoryId == (int)DepositCategoryTypes.Rent)
                        .Where(si => si.StatusId == (int)DepositStatus.Accepted || si.StatusId == (int)DepositStatus.SystemArchived)
                        .ToListAsync();

                    var saleDeposits = await dbcontext.Deposits.Include(ds => ds.DepositServices)
                        .Where(dci => dci.DepositCategoryId == (int)DepositCategoryTypes.Buy)
                        .Where(si => si.StatusId == (int)DepositStatus.Accepted || si.StatusId == (int)DepositStatus.SystemArchived)
                        .ToListAsync();

                    var Baseinfos = await redisCache.GetCacheValueAsync<List<Domain.Entities.BaseInfos.BaseInfo>>(CleanArc.Web.RedisCache.Services.RedisKeys.BaseInfoKey);

                    if (Baseinfos is null)
                    {
                        Baseinfos = await dbcontext.BaseInfos.ToListAsync();
                        await redisCache.SetCacheValueAsync(CleanArc.Web.RedisCache.Services.RedisKeys.BaseInfoKey, Baseinfos);
                    }

                    var saleDurationTime = Convert.ToDouble(Baseinfos.FirstOrDefault(f => f.Id == (int)AdDisplayDuration.Buy).DisplayTitle);
                    var rentDurationTime = Convert.ToDouble(Baseinfos.FirstOrDefault(f => f.Id == (int)AdDisplayDuration.Rent).DisplayTitle);

                    var dateOfRentExpire = DateTime.Now.AddDays(-rentDurationTime);
                    var dateOfSaleExpire = DateTime.Now.AddDays(-saleDurationTime);

                    var rentTask = Task.Run(() =>
                    {
                        foreach (var item in rentDeposits)
                        {
                            var DepositService = item.DepositServices
                                .Where(sti => sti.ServiceTypeId == (int)ServiceType.Renewal && sti.Disabled != true).FirstOrDefault();

                            if (DepositService == null)
                            {
                                DepositService = item.DepositServices
                                    .Where(sti => sti.ServiceTypeId == (int)ServiceType.RentalAdvertisement && sti.Disabled != true).FirstOrDefault();
                            }

                            var ShouldBeSystemArchived = DepositService.CreatedTime < dateOfRentExpire;

                            var StatusId = ShouldBeSystemArchived ? (int)DepositStatus.SystemArchived : (int)DepositStatus.Accepted;

                            if (item.StatusId != StatusId) item.StatusId = StatusId;

                        }
                    });

                    var saleTask = Task.Run(() =>
                    {
                        foreach (var item in saleDeposits)
                        {
                            var DepositService = item.DepositServices
                                .Where(sti => sti.ServiceTypeId == (int)ServiceType.Renewal && sti.Disabled != true).FirstOrDefault();

                            if (DepositService == null)
                            {
                                DepositService = item.DepositServices
                                    .Where(sti => sti.ServiceTypeId == (int)ServiceType.SalesAdvertisement && sti.Disabled != true).FirstOrDefault();
                            }

                            var ShouldBeSystemArchived = DepositService.CreatedTime < dateOfSaleExpire;

                            var StatusId = ShouldBeSystemArchived ? (int)DepositStatus.SystemArchived : (int)DepositStatus.Accepted;

                            if (item.StatusId != StatusId) item.StatusId = StatusId;

                        }
                    });

                    await Task.WhenAll(rentTask, saleTask);

                    await dbcontext.SaveChangesAsync();

                    DateTime now = DateTime.Now;
                    DateTime nextMidNight = now.Date.AddDays(1);
                    TimeSpan delay = nextMidNight - now;

                    await Task.Delay(delay, stoppingToken);
                }
                catch (Exception Ex)
                {
                    throw new Exception(Ex.Message);
                }
            }
        }
    }
}
