#!/bin/sh

set -eu

if [ $# -ne 2 ]
then
	echo "$0 <machine> <subdomain>"
	exit
fi

machine=$1
sub=$2

projectdir=..
publishdir=$projectdir/bin/Release/net8.0/linux-x64/publish
remotedir=/var/www/bloxstraplabs.com/$sub
service=$sub.bloxstraplabs.com.service

rm -r $publishdir
dotnet publish $projectdir -c Release -r linux-x64 --no-self-contained
dotnet ef migrations bundle -p $projectdir -r linux-x64 -o $publishdir/efbundle
scp -r $publishdir $machine:$remotedir.tmp
ssh -t $machine "sudo systemctl stop $service;\
cp -p $remotedir/appsettings.Production.json $remotedir.tmp/appsettings.Production.json;\
rm -r $remotedir;\
mv $remotedir.tmp $remotedir;\
sudo systemctl start $service;\
systemctl status $service;"