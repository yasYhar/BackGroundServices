using CleanArc.Application.Models.Dto.BackgroundService.SystemArchiveService;
using CleanArc.Domain.Entities.BaseInfos.Enums;
using CleanArc.Domain.Entities.Deposits;
using CleanArc.Domain.Entities.Deposits.Enums;
using CleanArc.Domain.Entities.Services.Enums;
using CleanArc.Infrastructure.Persistence;
using CleanArc.Web.RedisCache.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CleanArc.Infrastructure.BackgroundServices.DepositServices
{
    public class SystemArchiveBackgroundService(IServiceScopeFactory scopeFactory, IRedisCacheService redisCache) : BackgroundService
    {
        private bool isRunning = false;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            DateTime lastRunDate = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var lastExecute = await redisCache.GetCacheValueAsync<LastExecuteBackgroundServiceModel>(CleanArc.Application.Services.Cache.RedisKeys.LastExecuteSystemArchived);

                if (lastRunDate.Date < now.Date && (now.Hour > 1 && now.Hour < 2) && !isRunning && (lastExecute == null || lastExecute.LastExecute.Date != now.Date))
                {
                    isRunning = true;
                    await Execute(stoppingToken);

                    lastRunDate = now.Date;

                    await redisCache.SetCacheValueAsync<LastExecuteBackgroundServiceModel>
                        (CleanArc.Application.Services.Cache.RedisKeys.LastExecuteSystemArchived, new LastExecuteBackgroundServiceModel { LastExecute = DateTime.Now });
                    isRunning = false;
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private async Task Execute(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbcontext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                List<Deposit> rentDeposits = await GetDeposit(dbcontext, DepositCategoryTypes.Rent);
                List<Deposit> saleDeposits = await GetDeposit(dbcontext, DepositCategoryTypes.Buy);

                var Durations = await CalculateDuration(dbcontext);

                await SystemArchive(rentDeposits, Durations.ExpireTimeOfRent, ServiceType.RentalAdvertisement);
                await SystemArchive(saleDeposits, Durations.ExpireTimeOfSale, ServiceType.SalesAdvertisement);

                await dbcontext.SaveChangesAsync();
            }
            catch (Exception Ex)
            {
                Console.WriteLine("Error", Ex.ToString());
                isRunning = false;
            }
        }

        private static async Task<List<Deposit>> GetDeposit(ApplicationDbContext dbcontext, DepositCategoryTypes categoryTypes)
        {
            return await dbcontext.Deposits.Include(ds => ds.DepositServices)
                                .Where(dci => dci.DepositCategoryId == (int)categoryTypes)
                                .Where(si => si.StatusId == (int)DepositStatus.Accepted || si.StatusId == (int)DepositStatus.SystemArchived)
                                .Where(d => d.Disabled == false && d.IsDeleted == false)
                                .ToListAsync();
        }

        private async Task<DurationTimeDto> CalculateDuration(ApplicationDbContext dbcontext)
        {
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

            return new DurationTimeDto
            {
                ExpireTimeOfRent = dateOfRentExpire,
                ExpireTimeOfSale = dateOfSaleExpire,
            };
        }

        private async Task SystemArchive(List<Deposit>? deposits, DateTime dateOfExpire, ServiceType serviceType)
        {
            foreach (var item in deposits)
            {
                var DepositServices = item.DepositServices
                    .Where(ct => ct.CreatedTime >= dateOfExpire).ToList();

                var Service = DepositServices
                    .Where(sti => sti.ServiceTypeId == (int)ServiceType.Renewal && sti.Disabled != true).FirstOrDefault();

                if (Service == null)
                {
                    Service = DepositServices
                        .Where(sti => sti.ServiceTypeId == (int)serviceType && sti.Disabled != true).FirstOrDefault();
                }

                var ShouldBeSystemArchived = Service == null;

                var StatusId = ShouldBeSystemArchived ? (int)DepositStatus.SystemArchived : (int)DepositStatus.Accepted;

                if (item.StatusId != StatusId) item.StatusId = StatusId;
            }
            ;
        }
    }

    public class LastExecuteBackgroundServiceModel
    {
        public DateTime LastExecute { get; set; }
    }
}