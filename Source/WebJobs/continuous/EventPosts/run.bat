@echo off
%WEBROOT_PATH%\App_Data\JobRunner\Job.bat -t "Exceptionless.Core.Jobs.EventPostsJob, Exceptionless.Core" -c -s "Exceptionless.Core.Jobs.JobBootstrapper, Exceptionless.Core"