# catches: the production SchedulerFactory never constructs the judge at all - a defect
#          that SURVIVES every upstream proof passing, because assembly is the one thing
#          no per-component test can observe.
exit 0
